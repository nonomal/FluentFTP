﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using FluentFTP.Helpers;
using FluentFTP.Exceptions;
using System.Threading;
using System.Threading.Tasks;
using System.Data;
using FluentFTP.Rules;
using FluentFTP.Client.Modules;

namespace FluentFTP {
	public partial class AsyncFtpClient {

		/// <summary>
		/// Uploads the given file paths to a single folder on the server asynchronously.
		/// All files are placed directly into the given folder regardless of their path on the local filesystem.
		/// High-level API that takes care of various edge cases internally.
		/// Supports very large files since it uploads data in chunks.
		/// Faster than uploading single files with <see cref="o:UploadFile"/> since it performs a single "file exists" check rather than one check per file.
		/// </summary>
		/// <param name="localPaths">The full or relative paths to the files on the local file system. Files can be from multiple folders.</param>
		/// <param name="remoteDir">The full or relative path to the directory that files will be uploaded on the server</param>
		/// <param name="existsMode">What to do if the file already exists? Skip, overwrite or append? Set this to <see cref="FtpRemoteExists.NoCheck"/> for fastest performance,
		///  but only if you are SURE that the files do not exist on the server.</param>
		/// <param name="createRemoteDir">Create the remote directory if it does not exist.</param>
		/// <param name="verifyOptions">Sets verification behaviour and what to do if verification fails (See Remarks)</param>
		/// <param name="errorHandling">Used to determine how errors are handled</param>
		/// <param name="token">The token that can be used to cancel the entire process</param>
		/// <param name="progress">Provide an implementation of IProgress to track upload progress.</param>
		/// <param name="rules">Only files that pass all these rules are uploaded, and the files that don't pass are skipped.</param>
		/// <remarks>
		/// If verification is enabled (All options other than <see cref="FtpVerify.None"/>) the file will be verified against the source using the verification methods specified by <see cref="FtpVerifyMethod"/> in the client config.
		/// <br/> If only <see cref="FtpVerify.OnlyVerify"/> is set then the return of this method depends on both a successful transfer &amp; verification.
		/// <br/> Additionally, if any verify option is set and a retry is attempted the existsMode will automatically be set to <see cref="FtpRemoteExists.Overwrite"/>.
		/// If <see cref="FtpVerify.Throw"/> is set and <see cref="FtpError.Throw"/> is <i>not set</i>, then individual verification errors will not cause an exception to propagate from this method.
		/// </remarks>
		/// <returns>
		/// Returns a listing of all the local files, indicating if they were uploaded, skipped or overwritten.
		/// Returns a blank list if nothing was transferred. Never returns null.
		/// </returns>
		public async Task<List<FtpResult>> UploadFiles(IEnumerable<string> localPaths, string remoteDir, FtpRemoteExists existsMode = FtpRemoteExists.Overwrite, bool createRemoteDir = true,
			FtpVerify verifyOptions = FtpVerify.None, FtpError errorHandling = FtpError.None, CancellationToken token = default(CancellationToken),
			IProgress<FtpProgress> progress = null, List<FtpRule> rules = null) {

			// verify args
			if (!errorHandling.IsValidCombination()) {
				throw new ArgumentException("Invalid combination of FtpError flags.  Throw & Stop cannot be combined");
			}

			if (remoteDir.IsBlank()) {
				throw new ArgumentException("Required parameter is null or blank.", nameof(remoteDir));
			}

			remoteDir = remoteDir.GetFtpPath();

			LogFunction(nameof(UploadFiles), new object[] { localPaths, remoteDir, existsMode, createRemoteDir, verifyOptions, errorHandling });

			//check if cancellation was requested and throw to set TaskStatus state to Canceled
			token.ThrowIfCancellationRequested();

			remoteDir = await GetAbsolutePathAsync(remoteDir, token); // a dir is just like a path

			//flag to determine if existence checks are required
			var checkFileExistence = true;

			// create remote dir if wanted
			if (createRemoteDir) {
				if (!await DirectoryExists(remoteDir, token)) {
					await CreateDirectory(remoteDir, token);
					checkFileExistence = false;
				}
			}

			// result vars
			var errorEncountered = false;
			var successfulUploads = new List<string>();
			var results = new List<FtpResult>();
			var shouldExist = new Dictionary<string, bool>(); // paths of the files that should exist (lowercase for CI checks)

			// check which files should be uploaded or filtered out based on rules
			var filesToUpload = await GetFilesToUpload2Async(localPaths, remoteDir, rules, results, shouldExist, token);

			// get all the already existing files (if directory was created just create an empty array)
			var existingFiles = checkFileExistence ? await GetNameListing(remoteDir, token) : Array.Empty<string>();

			// per local file
			var r = -1;
			foreach (var result in filesToUpload) {
				r++;

				// check if cancellation was requested and throw to set TaskStatus state to Canceled
				token.ThrowIfCancellationRequested();

				// create meta progress to store the file progress
				var metaProgress = new FtpProgress(localPaths.Count(), r);

				// try to upload it
				try {
					var ok = await UploadFileFromFile(result.LocalPath, result.RemotePath, false, existsMode, FileListings.FileExistsInNameListing(existingFiles, result.RemotePath), true, verifyOptions, token, progress, metaProgress);

					// mark that the file succeeded
					result.IsSuccess = ok.IsSuccess();
					result.IsSkipped = ok.IsSkipped();
					result.IsFailed = ok.IsFailure();

					if (ok.IsSuccess()) {
						successfulUploads.Add(result.RemotePath);
					}
					else if ((int)errorHandling > 1) {
						errorEncountered = true;
						break;
					}
				}
				catch (Exception ex) {

					// mark that the file failed
					result.IsFailed = true;
					result.Exception = ex;

					if (ex is OperationCanceledException) {
						// DO NOT SUPPRESS CANCELLATION REQUESTS -- BUBBLE UP!
						LogWithPrefix(FtpTraceLevel.Info, "Upload cancellation requested");
						throw;
					}

					// suppress all other upload exceptions (errors are still written to FtpTrace)
					LogWithPrefix(FtpTraceLevel.Error, "Upload Failure for " + result.LocalPath, ex);
					if (errorHandling.HasFlag(FtpError.Stop)) {
						errorEncountered = true;
						break;
					}

					if (errorHandling.HasFlag(FtpError.Throw)) {
						if (errorHandling.HasFlag(FtpError.DeleteProcessed)) {
							await PurgeSuccessfulUploadsAsync(successfulUploads);
						}

						throw new FtpException("An error occurred uploading file(s).  See inner exception for more info.", ex);
					}
				}
			}

			if (errorEncountered) {
				// Delete any successful uploads if needed
				if (errorHandling.HasFlag(FtpError.DeleteProcessed)) {
					await PurgeSuccessfulUploadsAsync(successfulUploads);
					successfulUploads.Clear(); //forces return of 0
				}

				// Throw generic error because requested
				if (errorHandling.HasFlag(FtpError.Throw)) {
					throw new FtpException("An error occurred uploading one or more files.  Refer to trace output if available.");
				}
			}

			return results;
		}

		/// <summary>
		/// Remove successfully uploaded files.
		/// </summary>
		protected async Task PurgeSuccessfulUploadsAsync(IEnumerable<string> remotePaths) {
			foreach (var remotePath in remotePaths) {
				await DeleteFile(remotePath);
			}
		}

		/// <summary>
		/// Uploads the given file paths to a single folder on the server asynchronously.
		/// All files are placed directly into the given folder regardless of their path on the local filesystem.
		/// High-level API that takes care of various edge cases internally.
		/// Supports very large files since it uploads data in chunks.
		/// Faster than uploading single files with <see cref="o:UploadFile"/> since it performs a single "file exists" check rather than one check per file.
		/// </summary>
		/// <param name="localFiles">The full or relative paths to the files on the local file system. Files can be from multiple folders.</param>
		/// <param name="remoteDir">The full or relative path to the directory that files will be uploaded on the server</param>
		/// <param name="existsMode">What to do if the file already exists? Skip, overwrite or append? Set this to <see cref="FtpRemoteExists.NoCheck"/> for fastest performance,
		///  but only if you are SURE that the files do not exist on the server.</param>
		/// <param name="createRemoteDir">Create the remote directory if it does not exist.</param>
		/// <param name="verifyOptions">Sets verification behaviour and what to do if verification fails (See Remarks)</param>
		/// <param name="errorHandling">Used to determine how errors are handled</param>
		/// <param name="token">The token that can be used to cancel the entire process</param>
		/// <param name="progress">Provide an implementation of IProgress to track upload progress.</param>
		/// <param name="rules">Only files that pass all these rules are uploaded, and the files that don't pass are skipped.</param>
		/// <remarks>
		/// If verification is enabled (All options other than <see cref="FtpVerify.None"/>) the file will be verified against the source using the verification methods specified by <see cref="FtpVerifyMethod"/> in the client config.
		/// <br/> If only <see cref="FtpVerify.OnlyVerify"/> is set then the return of this method depends on both a successful transfer &amp; verification.
		/// <br/> Additionally, if any verify option is set and a retry is attempted the existsMode will automatically be set to <see cref="FtpRemoteExists.Overwrite"/>.
		/// If <see cref="FtpVerify.Throw"/> is set and <see cref="FtpError.Throw"/> is <i>not set</i>, then individual verification errors will not cause an exception to propagate from this method.
		/// </remarks>
		/// <returns>
		/// Returns a listing of all the local files, indicating if they were uploaded, skipped or overwritten.
		/// Returns a blank list if nothing was transferred. Never returns null.
		/// </returns>
		public async Task<List<FtpResult>> UploadFiles(IEnumerable<FileInfo> localFiles, string remoteDir, FtpRemoteExists existsMode = FtpRemoteExists.Overwrite, bool createRemoteDir = true,
			FtpVerify verifyOptions = FtpVerify.None, FtpError errorHandling = FtpError.None, CancellationToken token = default(CancellationToken),
			IProgress<FtpProgress> progress = null, List<FtpRule> rules = null) {
			return await UploadFiles(localFiles.Select(f => f.FullName), remoteDir, existsMode, createRemoteDir, verifyOptions, errorHandling, token, progress, rules);
		}

		/// <summary>
		/// Get a list of all the files that need to be uploaded
		/// </summary>
		protected async Task<List<FtpResult>> GetFilesToUpload2Async(IEnumerable<string> localFiles, string remoteDir, List<FtpRule> rules, List<FtpResult> results, Dictionary<string, bool> shouldExist, CancellationToken token) {

			var filesToUpload = new List<FtpResult>();

			foreach (var localPath in localFiles) {

				// calc remote path
				var fileName = Path.GetFileName(localPath);
				var remoteFilePath = "";
				remoteFilePath = await GetAbsoluteFilePathAsync(remoteDir, fileName, token);

				// record that this file should be uploaded
				FileUploadModule.RecordFileToUpload(this, rules, results, shouldExist, filesToUpload, localPath, remoteFilePath);
			}

			return filesToUpload;
		}

	}
}
