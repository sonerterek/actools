using System;
using System.Diagnostics;
using System.IO;

namespace AcManager.UiObserver
{
	/// <summary>
	/// Centralized debug logging for Navigator, StreamDeck, and Wheel Navigation.
	/// Writes all Trace.WriteLine() output to the host-owned trace file.
	/// ✅ Works in BOTH Debug and Release builds (uses Trace, not Debug).
	/// ✅ ALSO provides direct logging API (WriteLine) that bypasses Trace infrastructure.
	/// </summary>
	public static class DebugLog
	{
		private static TextWriter _logWriter;
		private static bool _initialized = false;
		private static readonly object _lock = new object();
		private static string _logFilePath;

		/// <summary>
		/// Configures debug logging with a host-owned trace writer.
		/// </summary>
		public static void Initialize(TextWriter logWriter, string logFilePath)
		{
			lock (_lock)
			{
				if (logWriter == null) throw new ArgumentNullException(nameof(logWriter));
				if (string.IsNullOrWhiteSpace(logFilePath)) throw new ArgumentException("A host trace-file path is required.", nameof(logFilePath));
				if (_initialized && !ReferenceEquals(_logWriter, logWriter))
					throw new InvalidOperationException("Navigator logging is already configured for this process.");
				if (_initialized) return;

				_logWriter = logWriter;
				_logFilePath = logFilePath;
				_initialized = true;
				Trace.Listeners.Add(new TextWriterTraceListener(_logWriter));

				WriteLine("═══════════════════════════════════════════════════════════");
				WriteLine($"[DebugLog] Logging initialized: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
				WriteLine($"[DebugLog] Log file: {_logFilePath}");
				WriteLine($"[DebugLog] Build configuration: {GetBuildConfiguration()}");
				WriteLine("═══════════════════════════════════════════════════════════");
				WriteLine("");
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
		/// Historical-file retention is owned by the host trace session.
		/// </summary>
		public static void CleanupOldLogs()
		{
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
