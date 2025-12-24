using CommandLine;
using streamdeck_client_csharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NWRS_AC_SDPlugin
{
	/// <summary>
	/// Represents a key definition from Content Manager
	/// </summary>
	public class KeyDefinition
	{
		public string Name { get; set; }
		public string IconFileName { get; set; }
		public string Title { get; set; }
	}

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

		// Registry for key definitions
		private static Dictionary<string, KeyDefinition> _keyDefinitions = new Dictionary<string, KeyDefinition>();

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
				// Clean up when client disconnects
				Debug.WriteLine("🧹 NamedPipe: Cleaning up client session");
				
				// Unwire key press events
				VKey.OnKeyPressedExternal -= keyPressHandler;
				
				// Clear all key definitions and pages
				_keyDefinitions.Clear();
				VPages.ClearAll();
				
				// Deactivate SDeck
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

				Debug.WriteLine($"🔧 NamedPipe: Processing command '{commandName}'");

				switch (commandName)
				{
					case "DEFINEKEY":
						HandleDefineKey(args);
						break;

					case "DEFINEPAGE":
						HandleDefinePage(args);
						break;

					case "SETKEYVISUALS":
						HandleSetKeyVisuals(args);
						break;

					case "SWITCHPAGE":
						HandleSwitchPage(args);
						break;

					case "SWITCHPROFILE":
						HandleSwitchProfile(args);
						break;

					case "SWITCHPROFILEBACK":
						HandleSwitchProfileBack();
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
		/// Handle DefineKey command from Content Manager
		/// Format: "DefineKey KeyName [Title] [IconFileName]"
		/// Note: Title and IconFileName are both optional - can create blank keys
		/// </summary>
		private static void HandleDefineKey(string args)
		{
			var parts = ParseCommandArgs(args, 3);
			
			if (parts.Length < 1)
			{
				Debug.WriteLine("⚠️ NamedPipe: DefineKey requires: KeyName [Title] [IconFileName]");
				_ = SendKeyDefinedEvent("unknown", false, "Missing required parameter: KeyName required");
				return;
			}
			
			string keyName = parts[0];
			
			try
			{
				// Title is optional - can be null/empty for icon-only or blank keys
				string title = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) && parts[1] != "null"
					? parts[1]
					: null;
				
				// IconFileName is optional - can be null/empty for title-only or blank keys
				string iconFileName = parts.Length > 2 && !string.IsNullOrEmpty(parts[2]) && parts[2] != "null" 
					? parts[2] 
					: null;
				
				var keyDef = new KeyDefinition
				{
					Name = keyName,
					IconFileName = iconFileName,
					Title = title
				};
				
				_keyDefinitions[keyDef.Name] = keyDef;
				_ = SendKeyDefinedEvent(keyName, true);
				
				if (iconFileName == null && title == null)
				{
					Debug.WriteLine($"✅ NamedPipe: Defined blank key '{keyDef.Name}'");
				}
				else if (iconFileName == null)
				{
					Debug.WriteLine($"✅ NamedPipe: Defined title-only key '{keyDef.Name}' with title '{keyDef.Title}'");
				}
				else if (title == null)
				{
					Debug.WriteLine($"✅ NamedPipe: Defined icon-only key '{keyDef.Name}' with icon '{keyDef.IconFileName}'");
				}
				else
				{
					Debug.WriteLine($"✅ NamedPipe: Defined key '{keyDef.Name}' with title '{keyDef.Title}' and icon '{keyDef.IconFileName}'");
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ NamedPipe: Error defining key '{keyName}': {ex.Message}");
				_ = SendKeyDefinedEvent(keyName, false, ex.Message);
			}
		}

		/// <summary>
		/// Handle DefinePage command from Content Manager
		/// Format: "DefinePage PageName [[key00,key01,key02],[key10,key11,key12],...]"
		/// </summary>
		private static void HandleDefinePage(string args)
		{
			var parts = args.Split(new[] { ' ' }, 2);
			if (parts.Length < 2)
			{
				Debug.WriteLine("⚠️ NamedPipe: DefinePage requires: PageName KeyGrid");
				_ = SendPageDefinedEvent("unknown", false, "Missing required parameters: PageName and KeyGrid required");
				return;
			}
			
			string pageName = parts[0];
			string keyGridJson = parts[1];
			
			try
			{
				// Parse JSON grid
				var keyGrid = ParseKeyGrid(keyGridJson);
				if (keyGrid == null)
				{
					Debug.WriteLine($"⚠️ NamedPipe: Failed to parse key grid for page '{pageName}'");
					_ = SendPageDefinedEvent(pageName, false, "Invalid JSON grid format");
					return;
				}
				
				// Validate ALL keys exist before creating page
				int gridRows = keyGrid.GetLength(0);
				int gridCols = keyGrid.GetLength(1);
				var missingKeys = new List<string>();
				
				for (int r = 0; r < Math.Min(5, gridRows); r++)
				{
					for (int c = 0; c < Math.Min(3, gridCols); c++)
					{
						string keyName = keyGrid[r, c];
						if (!string.IsNullOrEmpty(keyName) && keyName != "null")
						{
							if (!_keyDefinitions.ContainsKey(keyName))
							{
								missingKeys.Add($"{keyName}@({r},{c})");
							}
						}
					}
				}
				
				// If ANY keys are missing, fail the entire page definition
				if (missingKeys.Count > 0)
				{
					var errorMsg = $"Undefined keys: {string.Join(", ", missingKeys)}";
					Debug.WriteLine($"❌ NamedPipe: Page '{pageName}' failed - {errorMsg}");
					_ = SendPageDefinedEvent(pageName, false, errorMsg);
					return;
				}
				
				// All keys exist - create the page
				var vPage = new VPage(pageName, rows: 5, cols: 3);
				
				for (int r = 0; r < Math.Min(5, gridRows); r++)
				{
					for (int c = 0; c < Math.Min(3, gridCols); c++)
					{
						string keyName = keyGrid[r, c];
						if (!string.IsNullOrEmpty(keyName) && keyName != "null")
						{
							var keyDef = _keyDefinitions[keyName];
							
							// Create VKey with action that triggers external event
							new VKey(
								name: keyDef.Name,
								vPage: vPage,
								row: r,
								col: c,
								onPress: () => { /* Action handled by OnKeyPressedExternal */ },
								minPress: 0,
								image: keyDef.IconFileName,
								title: keyDef.Title
							);
						}
					}
				}
				
				_ = SendPageDefinedEvent(pageName, true);
				Debug.WriteLine($"✅ NamedPipe: Created page '{pageName}' (5x3 grid)");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ NamedPipe: Error creating page '{pageName}': {ex.Message}");
				_ = SendPageDefinedEvent(pageName, false, ex.Message);
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
			
			var vPage = VPages.GetByName(pageName);
			if (vPage != null)
			{
				SDeck.SetVPage(vPage);
				Debug.WriteLine($"✅ NamedPipe: Switched to page '{pageName}'");
			}
			else
			{
				Debug.WriteLine($"⚠️ NamedPipe: Page '{pageName}' not found");
			}
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
		/// Handle SwitchProfileBack command from Content Manager
		/// Format: "SwitchProfileBack"
		/// </summary>
		private static void HandleSwitchProfileBack()
		{
			Debug.WriteLine($"🔙 NamedPipe: Switching back to previous profile");
			SDeck.SwitchBackToPreviousProfile();
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

		/// <summary>
		/// Send KeyDefined event to Content Manager
		/// Format: "KeyDefined KeyName OK" or "KeyDefined KeyName ERROR message"
		/// </summary>
		private static async Task SendKeyDefinedEvent(string keyName, bool success, string errorMessage = null)
		{
			if (_pipeWriter == null || !_pipeServer.IsConnected)
			{
				Debug.WriteLine("⚠️ NamedPipe: Cannot send event - not connected");
				return;
			}

			try
			{
				var status = success ? "OK" : "ERROR";
				var eventMessage = success 
					? $"KeyDefined {keyName} {status}"
					: $"KeyDefined {keyName} {status} {errorMessage}";
				
				await _pipeWriter.WriteLineAsync(eventMessage);
				Debug.WriteLine($"📤 NamedPipe: Sent event: {eventMessage}");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ NamedPipe: Error sending KeyDefined event: {ex.Message}");
			}
		}

		/// <summary>
		/// Send PageDefined event to Content Manager
		/// Format: "PageDefined PageName OK" or "PageDefined PageName ERROR message"
		/// </summary>
		private static async Task SendPageDefinedEvent(string pageName, bool success, string errorMessage = null)
		{
			if (_pipeWriter == null || !_pipeServer.IsConnected)
			{
				Debug.WriteLine("⚠️ NamedPipe: Cannot send event - not connected");
				return;
			}

			try
			{
				var status = success ? "OK" : "ERROR";
				var eventMessage = success 
					? $"PageDefined {pageName} {status}"
					: $"PageDefined {pageName} {status} {errorMessage}";
				
				await _pipeWriter.WriteLineAsync(eventMessage);
				Debug.WriteLine($"📤 NamedPipe: Sent event: {eventMessage}");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ NamedPipe: Error sending PageDefined event: {ex.Message}");
			}
		}

		/// <summary>
		/// Send KeyVisualsSet event to Content Manager
		/// Format: "KeyVisualsSet KeyName OK" or "KeyVisualsSet KeyName ERROR message"
		/// </summary>
		private static async Task SendKeyVisualsSetEvent(string keyName, bool success, string errorMessage = null)
		{
			if (_pipeWriter == null || !_pipeServer.IsConnected)
			{
				Debug.WriteLine("⚠️ NamedPipe: Cannot send event - not connected");
				return;
			}

			try
			{
				var status = success ? "OK" : "ERROR";
				var eventMessage = success 
					? $"KeyVisualsSet {keyName} {status}"
					: $"KeyVisualsSet {keyName} {status} {errorMessage}";
				
				await _pipeWriter.WriteLineAsync(eventMessage);
				Debug.WriteLine($"📤 NamedPipe: Sent event: {eventMessage}");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ NamedPipe: Error sending KeyVisualsSet event: {ex.Message}");
			}
		}

		/// <summary>
		/// Parse command arguments, respecting quoted strings
		/// Example: "key1 icon.png \"My Title\"" → ["key1", "icon.png", "My Title"]
		/// </summary>
		private static string[] ParseCommandArgs(string args, int maxParts)
		{
			var result = new List<string>();
			bool inQuotes = false;
			var current = new StringBuilder();
			
			for (int i = 0; i < args.Length; i++)
			{
				char c = args[i];
				
				if (c == '"')
				{
					inQuotes = !inQuotes;
				}
				else if (c == ' ' && !inQuotes)
				{
					if (current.Length > 0)
					{
						result.Add(current.ToString());
						current.Clear();
						
						if (result.Count == maxParts - 1 && i < args.Length - 1)
						{
							// Last part gets everything remaining
							result.Add(args.Substring(i + 1).Trim().Trim('"'));
							break;
						}
					}
				}
				else
				{
					current.Append(c);
				}
			}
			
			if (current.Length > 0)
				result.Add(current.ToString());
			
			return result.ToArray();
		}

		/// <summary>
		/// Parse key grid JSON format
		/// Example: "[[key00,key01,key02],[key10,key11,key12]]"
		/// Returns 5x3 grid (StreamDeck standard size)
		/// </summary>
		private static string[,] ParseKeyGrid(string json)
		{
			try
			{
				var rows = JsonSerializer.Deserialize<string[][]>(json);
				
				// Always create 5x3 grid
				var grid = new string[5, 3];
				
				// Fill grid from parsed JSON (may be smaller)
				for (int r = 0; r < Math.Min(5, rows.Length); r++)
				{
					for (int c = 0; c < Math.Min(3, rows[r].Length); c++)
					{
						grid[r, c] = rows[r][c];
					}
				}
				
				return grid;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ NamedPipe: Failed to parse key grid: {ex.Message}");
				return null;
			}
		}

		/// <summary>
		/// Clean up named pipe resources
		/// </summary>
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

		/// <summary>
		/// Handle SetKeyVisuals command from Content Manager
		/// Format: "SetKeyVisuals KeyName [Title] [IconFileName]"
		/// Note: Updates only the key in the currently active page, not the shared definition
		/// Creates a new VKey instance to avoid affecting other pages
		/// </summary>
		private static void HandleSetKeyVisuals(string args)
		{
			var parts = ParseCommandArgs(args, 3);
			
			if (parts.Length < 1)
			{
				Debug.WriteLine("⚠️ NamedPipe: SetKeyVisuals requires: KeyName [Title] [IconFileName]");
				_ = SendKeyVisualsSetEvent("unknown", false, "Missing required parameter: KeyName required");
				return;
			}
			
			string keyName = parts[0];
			
			try
			{
				// Check if key exists in definitions
				if (!_keyDefinitions.ContainsKey(keyName))
				{
					Debug.WriteLine($"⚠️ NamedPipe: Key '{keyName}' not found in definitions");
					_ = SendKeyVisualsSetEvent(keyName, false, $"Key '{keyName}' not defined");
					return;
				}
				
				// Get the currently active page
				var currentPage = SDeck.SDPage?.VPage;
				if (currentPage == null)
				{
					Debug.WriteLine($"⚠️ NamedPipe: No active page to update key '{keyName}'");
					_ = SendKeyVisualsSetEvent(keyName, false, "No active page");
					return;
				}
				
				// Find the VKey in the current page
				VKey targetKey = null;
				int targetRow = -1, targetCol = -1;
				
				for (int r = 0; r < currentPage.Rows; r++)
				{
					for (int c = 0; c < currentPage.Cols; c++)
					{
						var vKey = currentPage.VKeys[r, c];
						if (vKey != null)
						{
							// Use reflection to get the private _name field
							var nameField = vKey.GetType().GetField("_name", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
							if (nameField?.GetValue(vKey) as string == keyName)
							{
								targetKey = vKey;
								targetRow = r;
								targetCol = c;
								break;
							}
						}
					}
					if (targetKey != null) break;
				}
				
				if (targetKey == null)
				{
					Debug.WriteLine($"⚠️ NamedPipe: Key '{keyName}' not found in current page '{currentPage.Name}'");
					_ = SendKeyVisualsSetEvent(keyName, false, $"Key '{keyName}' not found in current page");
					return;
				}
				
				// Parse new title and icon (both optional)
				string newTitle = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) && parts[1] != "null"
					? parts[1]
					: null;
				
				string newIcon = parts.Length > 2 && !string.IsNullOrEmpty(parts[2]) && parts[2] != "null"
					? parts[2]
					: null;
				
				// Create a new VKey instance with updated visuals
				// This ensures we don't modify the shared definition or affect other pages
				var newVKey = new VKey(
					name: keyName,
					vPage: currentPage,
					row: targetRow,
					col: targetCol,
					onPress: () => { /* Action handled by OnKeyPressedExternal */ },
					minPress: 0,
					image: newIcon,
					title: newTitle
				);
				
				_ = SendKeyVisualsSetEvent(keyName, true);
				
				Debug.WriteLine($"✅ NamedPipe: Updated visuals for key '{keyName}' in page '{currentPage.Name}' - Title: '{newTitle ?? "null"}', Icon: '{newIcon ?? "null"}'");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ NamedPipe: Error setting visuals for key '{keyName}': {ex.Message}");
				_ = SendKeyVisualsSetEvent(keyName, false, ex.Message);
			}
		}
	}
}
