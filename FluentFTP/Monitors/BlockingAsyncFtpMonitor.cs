﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FluentFTP.Monitors {

	/// <summary>
	/// An async FTP folder monitor that monitors specific remote folders on the FTP server.
	/// It triggers events when list items are added, changed or removed.
	/// Internally it polls the remote folder(s) every `PollInterval` and checks for changed files.
	/// If `WaitTillFileFullyUploaded` is true, then the list items is only detected as an added when the size is stable.
	///
	/// NOTE: This is user contributed code and uses an unusual async pattern.
	/// Refer to the original PR to understand the design principles:
	/// https://github.com/robinrodricks/FluentFTP/pull/1663
	/// </summary>
	/// 
	public class BlockingAsyncFtpMonitor : BaseFtpMonitor, IDisposable
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
		, IAsyncDisposable
#endif
		{

		private readonly AsyncFtpClient _ftpClient;

		/// <summary>
		/// Sets the handler that is called when changes are detected in the monitored folder(s)
		/// </summary>
		public Func<FtpMonitorEventArgs, Task> ChangeDetected;

		/// <summary>
		/// Create a new FTP monitor.
		/// Provide a valid FTP client, and then do not use this client for any other purpose.
		/// This FTP client would then be owned and controlled by this class.
		/// The client can be used in the handler to perform FTP operations.
		/// </summary>
		public BlockingAsyncFtpMonitor(AsyncFtpClient ftpClient, List<string> folderPaths) {
			_ftpClient = ftpClient ?? throw new ArgumentNullException(nameof(ftpClient));
			if (folderPaths == null || folderPaths.Count == 0) {
				throw new ArgumentNullException(nameof(folderPaths));
			}
			FolderPaths = folderPaths;
		}

		/// <summary>
		/// Monitor the FTP folder(s) until the token is cancelled
		/// or an exception occurs in the FTP client or the handler.
		/// </summary>
		public async Task Start(CancellationToken token) {
			while (true) {
				try {
					var startTimeUtc = DateTime.UtcNow;

					await PollFolder(token).ConfigureAwait(false);

					var pollInterval = _unstableFiles.Count > 0 && UnstablePollInterval != null ? UnstablePollInterval.Value : PollInterval;
					var waitTime = pollInterval - (DateTime.UtcNow - startTimeUtc);

					if (waitTime > TimeSpan.Zero) {
						await Task.Delay(waitTime, token).ConfigureAwait(false);
					}
					else {
						token.ThrowIfCancellationRequested();
					}
				}
				catch (OperationCanceledException)
					when (token.IsCancellationRequested) {
					break;
				}
			}
		}

		public void Dispose() {
			_ftpClient?.Dispose();
		}

#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
		public async ValueTask DisposeAsync() {
			if (_ftpClient != null) {
				await _ftpClient.DisposeAsync().ConfigureAwait(false);
			}
		}
#endif
		public override string ToString() {
			return $"FolderPaths = \"{string.Join("\",\"", FolderPaths)}\" PollInterval = {PollInterval} WaitTillFileFullyUploaded = {WaitTillFileFullyUploaded}";
		}

		/// <summary>
		/// Polls the FTP folder(s) for changes
		/// </summary>
		private async Task PollFolder(CancellationToken token) {

			// Step 1: Get the current listing
			var currentListing = await GetCurrentListing(token).ConfigureAwait(false);

			// Step 2: Handle unstable list items if WaitTillFileFullyUploaded is true
			if (WaitTillFileFullyUploaded) {
				currentListing = HandleUnstableFiles(currentListing);
			}

			// Step 3: Compare current listing to last listing
			var listItemsAdded = new List<string>();
			var listItemsChanged = new List<string>();

			foreach (var listItem in currentListing) {
				if (!_lastListing.TryGetValue(listItem.Key, out var lastItem)) {
					listItemsAdded.Add(listItem.Key);
				}
				else if (lastItem != listItem.Value) {
					listItemsChanged.Add(listItem.Key);
				}
			}
			var listItemsDeleted = _lastListing.Keys.Except(currentListing.Keys).ToList();

			// Step 4: Update last listing
			_lastListing = currentListing;

			if (listItemsAdded.Count == 0 && listItemsChanged.Count == 0 && listItemsDeleted.Count == 0) {
				return;
			}

			// Step 5: Raise event
			if (ChangeDetected == null) {
				return;
			}

			try {
				var args = new FtpMonitorEventArgs(listItemsAdded, listItemsChanged, listItemsDeleted, _ftpClient, null, token);
				await ChangeDetected(args).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
				when (token.IsCancellationRequested) {
			}
		}


		/// <summary>
		/// Gets the current list items from the FTP server
		/// </summary>
		private async Task<Dictionary<string, long>> GetCurrentListing(CancellationToken token) {
			var options = GetListingOptions(_ftpClient.Capabilities);

			// per folder to check
			var allItems = new Dictionary<string, long>();
			foreach (var folderPath in FolderPaths) {

				// get listing
				var items = await _ftpClient.GetListing(folderPath, options, token).ConfigureAwait(false);
				foreach (var f in items) {
					if (f.Type == FtpObjectType.File) {

						// combine it into the results
						allItems[f.FullName] = f.Size;
					}
				}
			}

			return allItems;
		}

	}
}