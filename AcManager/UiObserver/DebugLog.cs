using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AcManager.UiObserver
{
	/// <summary>
	/// Centralized debug logging for Navigator, StreamDeck, and Wheel Navigation.
	/// Writes all Trace.WriteLine() output to a timestamped log file.
	/// ✅ Works in BOTH Debug and Release builds (uses Trace, not Debug).
	/// ✅ ALSO provides direct logging API (WriteLine) that bypasses Trace infrastructure.
	/// </summary>
	public static class DebugLog
	{
		private static StreamWriter _logWriter;
		private static bool _initialized = false;
		private static readonly object _lock = new object();
		private static string _logFilePath;

		/// <summary>
		/// Initializes debug logging to file.
		/// Creates a timestamped log file in %LOCALAPPDATA%\AcTools Content Manager\Logs\
		/// Should be called once at application startup (in Navigator.Initialize()).
		/// </summary>
		public static void Initialize()
		{
			lock (_lock)
			{
				if (_initialized) return;

				try
				{
					// Create log directory
					var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
					var logDir = Path.Combine(appData, "AcTools Content Manager", "Logs");
					Directory.CreateDirectory(logDir);

					// Create timestamped log file
					_logFilePath = Path.Combine(logDir, $"Navigator_{DateTime.Now:yyyyMMdd_HHmmss}.log");

					// Create file stream (allow reading while we're writing)
					var fileStream = new FileStream(_logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
					_logWriter = new StreamWriter(fileStream) { AutoFlush = true };

					// Add listener to Trace output (works in both Debug and Release builds)
					var traceListener = new TextWriterTraceListener(_logWriter);
					Trace.Listeners.Add(traceListener);

					// Write header (using direct write to ensure it works)
					WriteLine("═══════════════════════════════════════════════════════════");
					WriteLine($"[DebugLog] Logging initialized: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
					WriteLine($"[DebugLog] Log file: {_logFilePath}");
					WriteLine($"[DebugLog] Build configuration: {GetBuildConfiguration()}");
					WriteLine("═══════════════════════════════════════════════════════════");
					WriteLine("");

					_initialized = true;
				}
				catch (Exception ex)
				{
					// Fallback: Write error to a backup log file
					try
					{
						var backupLog = Path.Combine(
							Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
							$"Navigator_InitError_{DateTime.Now:HHmmss}.txt"
						);
						File.WriteAllText(backupLog, 
							$"DebugLog.Initialize() FAILED: {ex.Message}\r\n{ex.StackTrace}");
					}
					catch
					{
						// Give up - can't log
					}
				}
			}
		}

		/// <summary>
		/// Direct write to log file (bypasses Trace infrastructure).
		/// ✅ Use this if Trace.WriteLine() isn't working.
		/// </summary>
		public static void WriteLine(string message)
		{
			lock (_lock)
			{
				try
				{
					if (_logWriter != null)
					{
						var timestamped = $"{DateTime.Now:HH:mm:ss.fff} {message}";
						_logWriter.WriteLine(timestamped);
						_logWriter.Flush(); // Force write
					}
				}
				catch
				{
					// Silently ignore write errors
				}
			}
		}

		/// <summary>
		/// Gets the current build configuration (Debug or Release).
		/// </summary>
		private static string GetBuildConfiguration()
		{
#if DEBUG
			return "Debug";
#else
			return "Release";
#endif
		}

		/// <summary>
		/// Cleans up old log files (keeps only last 7 days).
		/// Called automatically during initialization.
		/// </summary>
		public static void CleanupOldLogs()
		{
			try
			{
				var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
				var logDir = Path.Combine(appData, "AcTools Content Manager", "Logs");

				if (!Directory.Exists(logDir)) return;

				var cutoff = DateTime.Now.AddDays(-7);
				var oldLogs = Directory.GetFiles(logDir, "Navigator_*.log")
					.Where(f => File.GetCreationTime(f) < cutoff)
					.ToList();

				foreach (var log in oldLogs)
				{
					try
					{
						File.Delete(log);
						Trace.WriteLine($"[DebugLog] Deleted old log: {Path.GetFileName(log)}");
					}
					catch
					{
						// Ignore errors deleting old logs
					}
				}

				if (oldLogs.Count > 0)
				{
					Trace.WriteLine($"[DebugLog] Cleaned up {oldLogs.Count} old log file(s)");
				}
			}
			catch (Exception ex)
			{
				Trace.WriteLine($"[DebugLog] Error cleaning up old logs: {ex.Message}");
			}
		}

		/// <summary>
		/// Closes the log file and releases resources.
		/// Called automatically at application shutdown.
		/// </summary>
		public static void Shutdown()
		{
			lock (_lock)
			{
				if (!_initialized) return;

				try
				{
					Trace.WriteLine("");
					Trace.WriteLine("═══════════════════════════════════════════════════════════");
					Trace.WriteLine($"[DebugLog] Logging shutdown: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
					Trace.WriteLine("═══════════════════════════════════════════════════════════");

					_logWriter?.Flush();
					_logWriter?.Close();
					_logWriter = null;

					_initialized = false;
				}
				catch
				{
					// Ignore errors during shutdown
				}
			}
		}

		/// <summary>
		/// Gets the path to the current log file (for display in UI).
		/// Returns null if logging is not initialized.
		/// </summary>
		public static string GetCurrentLogPath()
		{
			lock (_lock)
			{
				return _logFilePath;
			}
		}
	}
}
