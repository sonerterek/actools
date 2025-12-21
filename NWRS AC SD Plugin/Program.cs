using CommandLine;
using streamdeck_client_csharp;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NWRS_AC_SDPlugin
{
    class Program
    {
        public class Options
        {
            [Option("port", Required = true, HelpText = "The websocket port to connect to", SetName = "port")]
            public int Port { get; set; }

            [Option("pluginUUID", Required = true, HelpText = "The UUID of the plugin")]
            public string PluginUUID { get; set; }

            [Option("registerEvent", Required = true, HelpText = "The event triggered when the plugin is registered?")]
            public string RegisterEvent { get; set; }

            [Option("info", Required = true, HelpText = "Extra JSON launch data")]
            public string Info { get; set; }
        }

		private const string PIPE_NAME = "NWRS_AC_SDPlugin_Pipe";
		private static NamedPipeServerStream _pipeServer;
		private static StreamReader _pipeReader;
		private static StreamWriter _pipeWriter;
		private static bool _isShuttingDown = false;

		// StreamDeck launches the plugin with these details
		// -port [number] -pluginUUID [GUID] -registerEvent [string?] -info [json]
		static void Main(string[] args)
		{
			// Uncomment this line of code to allow for debugging
			// while (!System.Diagnostics.Debugger.IsAttached) { System.Threading.Thread.Sleep(100); }

			// Log to Windows EventLog
			EventLog eventLog = new EventLog("NWRS", Environment.MachineName, "NWRS SDPlugin");

			// The command line args parser expects all args to use `--`, so, let's append
			for (int count = 0; count < args.Length; count++) {
				if (args[count].StartsWith("-") && !args[count].StartsWith("--")) {
					args[count] = $"-{args[count]}";
				}
			}

			Parser parser = new Parser((with) => {
				with.EnableDashDash = true;
				with.CaseInsensitiveEnumValues = true;
				with.CaseSensitive = false;
				with.IgnoreUnknownArguments = true;
				with.HelpWriter = Console.Error;
			});

			ParserResult<Options> options = parser.ParseArguments<Options>(args);

			options.WithParsed<Options>(o => {
				// Initialize SDeck with the command line parameters
				SDeck.Init(o.Port, o.PluginUUID, o.RegisterEvent);
				
				// Start the named pipe server for CM communication
				Task.Run(() => StartNamedPipeServer());
			});

			Thread.Sleep(Timeout.Infinite);
		}

		private static async Task StartNamedPipeServer()
		{
			while (!_isShuttingDown)
			{
				try
				{
					Debug.WriteLine($"🔗 NamedPipe: Creating server '{PIPE_NAME}'");
					
					_pipeServer = new NamedPipeServerStream(
						PIPE_NAME,
						PipeDirection.InOut,
						1, // Only 1 client (CM) at a time
						PipeTransmissionMode.Byte,
						PipeOptions.Asynchronous);

					Debug.WriteLine($"⏳ NamedPipe: Waiting for Content Manager to connect...");
					await _pipeServer.WaitForConnectionAsync();
					
					Debug.WriteLine($"✅ NamedPipe: Content Manager connected!");
					
					_pipeReader = new StreamReader(_pipeServer, Encoding.UTF8);
					_pipeWriter = new StreamWriter(_pipeServer, Encoding.UTF8) { AutoFlush = true };

					// Handle the connected client
					await HandleClientConnection();
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"❌ NamedPipe: Error: {ex.Message}");
				}
				finally
				{
					CleanupPipeResources();
					
					if (!_isShuttingDown)
					{
						Debug.WriteLine("🔄 NamedPipe: Restarting server for next connection...");
						await Task.Delay(1000); // Brief delay before restarting
					}
				}
			}
			
			Debug.WriteLine("🛑 NamedPipe: Server stopped");
		}

		private static async Task HandleClientConnection()
		{
			// Create a handler to send key press events
			Action<string> keyPressHandler = (keyName) => 
			{
				// Fire and forget - async void lambda
				_ = SendKeyPressEvent(keyName);
			};
			
			try
			{
				// Activate SDeck when client connects
				SDeck.Activate();
				
				// Wire up VKey key presses to send events to CM
				VKey.OnKeyPressedExternal += keyPressHandler;
				
				while (_pipeServer.IsConnected && !_isShuttingDown)
				{
					string command = await _pipeReader.ReadLineAsync();
					
					if (command == null)
					{
						Debug.WriteLine("⚠️ NamedPipe: Client disconnected (null command)");
						break;
					}

					Debug.WriteLine($"📩 NamedPipe: Received command: {command}");
					
					// Process the command (no response expected)
					ProcessCommand(command);
				}
			}
			catch (IOException ex)
			{
				Debug.WriteLine($"⚠️ NamedPipe: Client disconnected: {ex.Message}");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ NamedPipe: Error handling client: {ex.Message}");
			}
			finally
			{
				// Unwire key press events
				VKey.OnKeyPressedExternal -= keyPressHandler;
				
				// Deactivate SDeck when client disconnects
				SDeck.Deactivate();
			}
		}

		private static void ProcessCommand(string command)
		{
			if (string.IsNullOrWhiteSpace(command))
			{
				Debug.WriteLine("⚠️ NamedPipe: Received empty command");
				return;
			}

			try
			{
				// Parse command: "CommandName arg1 arg2 ..."
				var parts = command.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 0)
				{
					Debug.WriteLine("⚠️ NamedPipe: Invalid command format");
					return;
				}

				var commandName = parts[0].ToUpperInvariant();
				var args = parts.Length > 1 ? parts[1] : string.Empty;

				Debug.WriteLine($"🔧 NamedPipe: Processing command '{commandName}' with args '{args}'");

				switch (commandName)
				{
					case "SWITCHPAGE":
						HandleSwitchPage(args);
						break;

					case "SWITCHPROFILE":
						HandleSwitchProfile(args);
						break;

					default:
						Debug.WriteLine($"⚠️ NamedPipe: Unknown command '{commandName}'");
						break;
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ NamedPipe: Error processing command '{command}': {ex.Message}");
			}
		}

		/// <summary>
		/// Handle SwitchPage command from Content Manager
		/// Format: "SwitchPage PageName"
		/// </summary>
		private static void HandleSwitchPage(string pageName)
		{
			if (string.IsNullOrWhiteSpace(pageName))
			{
				Debug.WriteLine("⚠️ NamedPipe: SwitchPage requires a page name");
				return;
			}

			Debug.WriteLine($"📄 NamedPipe: Switching to page '{pageName}'");
			
			// TODO: Implement VPage lookup and switching
			// Example:
			// var vPage = VPages.GetByName(pageName);
			// if (vPage != null)
			// {
			//     SDeck.SetVPage(vPage);
			// }
			// else
			// {
			//     Debug.WriteLine($"⚠️ NamedPipe: Page '{pageName}' not found");
			// }
		}

		/// <summary>
		/// Handle SwitchProfile command from Content Manager
		/// Format: "SwitchProfile ProfileName"
		/// </summary>
		private static void HandleSwitchProfile(string profileName)
		{
			if (string.IsNullOrWhiteSpace(profileName))
			{
				Debug.WriteLine("⚠️ NamedPipe: SwitchProfile requires a profile name");
				return;
			}

			Debug.WriteLine($"🎮 NamedPipe: Switching to profile '{profileName}'");
			SDeck.SwitchToProfile(profileName);
		}

		/// <summary>
		/// Send KeyPress event to Content Manager
		/// Format: "KeyPress KeyName"
		/// </summary>
		private static async Task SendKeyPressEvent(string keyName)
		{
			if (_pipeWriter == null || !_pipeServer.IsConnected)
			{
				Debug.WriteLine("⚠️ NamedPipe: Cannot send event - not connected");
				return;
			}

			try
			{
				var eventMessage = $"KeyPress {keyName}";
				await _pipeWriter.WriteLineAsync(eventMessage);
				Debug.WriteLine($"📤 NamedPipe: Sent event: {eventMessage}");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ NamedPipe: Error sending KeyPress event: {ex.Message}");
			}
		}

		private static void CleanupPipeResources()
		{
			try
			{
				_pipeReader?.Dispose();
				_pipeWriter?.Dispose();
				_pipeServer?.Dispose();
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"⚠️ NamedPipe: Error during cleanup: {ex.Message}");
			}
			
			_pipeReader = null;
			_pipeWriter = null;
			_pipeServer = null;
		}
	}
}
