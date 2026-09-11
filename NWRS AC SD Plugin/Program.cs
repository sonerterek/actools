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
	/// Represents a key definition sent by a client.
	/// </summary>
	public class KeyDefinition
	{
		public string Name { get; set; }
		public string IconFileName { get; set; }
		public string Title { get; set; }
	}

	/// <summary>
	/// Stores a raw DefinePage command so it can be replayed when a suspended client gains control.
	/// The raw args string is kept verbatim; the plugin re-runs HandleDefinePage with the client's
	/// local key registry populated, so inheritance resolution works correctly.
	/// </summary>
	internal class SavedPageDef
	{
		public string Args { get; set; }   // everything after "DefinePage "
	}

	/// <summary>
	/// Per-connection client state.  All layout state is isolated here; the global
	/// VPages/VKey/SDeck infrastructure only ever sees the controlling client's state.
	/// </summary>
	internal class ConnectedClient
	{
		// ---- identity -------------------------------------------------------
		public string ClientId  { get; set; } = "unknown";
		public int    Priority  { get; set; } = 0;

		// ---- pipe I/O -------------------------------------------------------
		public NamedPipeServerStream PipeServer { get; set; }
		public StreamReader          Reader     { get; set; }
		public StreamWriter          Writer     { get; set; }
		public readonly object       WriteLock  = new object();

		// ---- control state --------------------------------------------------
		public bool IsControlling { get; set; } = false;

		// ---- per-client layout storage (replayed on promote) ----------------
		public Dictionary<string, KeyDefinition> KeyDefs   { get; } = new Dictionary<string, KeyDefinition>();
		public List<SavedPageDef>                PageDefs  { get; } = new List<SavedPageDef>();
		public string                            CurrentPageName { get; set; } = null;
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
		private static bool _isShuttingDown = false;

		// ---- multi-client registry ------------------------------------------
		private static readonly List<ConnectedClient>  _clients     = new List<ConnectedClient>();
		private static readonly object                  _clientsLock = new object();

		// ---- legacy single-client fields kept only for the static helper methods
		//      that still reference them; they are redirected to the controlling client.
		private static ConnectedClient _controllingClient = null;

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

				// Start the named pipe server — accepts multiple clients concurrently
				Task.Run(() => StartNamedPipeServer());
			});

			Thread.Sleep(Timeout.Infinite);
		}

		// -----------------------------------------------------------------------
		// Named pipe server — one listener loop per slot, allowing N clients
		// -----------------------------------------------------------------------

		private static async Task StartNamedPipeServer()
		{
			while (!_isShuttingDown)
			{
				NamedPipeServerStream pipeServer = null;
				try
				{
					Debug.WriteLine($"🔗 NamedPipe: Creating server slot '{PIPE_NAME}'");

					pipeServer = new NamedPipeServerStream(
						PIPE_NAME,
						PipeDirection.InOut,
						NamedPipeServerStream.MaxAllowedServerInstances,
						PipeTransmissionMode.Byte,
						PipeOptions.Asynchronous);

					Debug.WriteLine("⏳ NamedPipe: Waiting for a client to connect...");
					await pipeServer.WaitForConnectionAsync();
					Debug.WriteLine("✅ NamedPipe: A client connected — handing off to HandleClientConnection");

					var reader = new StreamReader(pipeServer, Encoding.UTF8);
					var writer = new StreamWriter(pipeServer, Encoding.UTF8) { AutoFlush = true };

					var client = new ConnectedClient
					{
						PipeServer = pipeServer,
						Reader     = reader,
						Writer     = writer,
					};

					lock (_clientsLock)
						_clients.Add(client);

					// Each client runs on its own task
					_ = Task.Run(() => HandleClientConnection(client));
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"❌ NamedPipe: Listener error: {ex.Message}");
					pipeServer?.Dispose();
					if (!_isShuttingDown)
						await Task.Delay(1000);
				}
			}

			Debug.WriteLine("🛑 NamedPipe: Server stopped");
		}

		// -----------------------------------------------------------------------
		// Per-client connection handler
		// -----------------------------------------------------------------------

		private static async Task HandleClientConnection(ConnectedClient client)
		{
			// Key-press handler that only fires when this client is in control
			Action<string> keyPressHandler = (keyName) =>
			{
				if (_controllingClient == client)
					_ = SendKeyPressToClient(client, keyName);
			};

			try
			{
				// ---- first line MUST be: Hello <ClientId> <Priority> ----
				string hello = await client.Reader.ReadLineAsync();
				if (hello == null)
				{
					Debug.WriteLine("⚠️ NamedPipe: Client disconnected before Hello");
					return;
				}

				var helloParts = hello.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
				if (helloParts.Length < 3 || !helloParts[0].Equals("HELLO", StringComparison.OrdinalIgnoreCase))
				{
					Debug.WriteLine($"⚠️ NamedPipe: Expected 'Hello <ClientId> <Priority>', got: {hello}");
					return;
				}

				client.ClientId = helloParts[1];
				if (!int.TryParse(helloParts[2], out int prio))
					prio = 0;
				client.Priority = prio;
				Debug.WriteLine($"👋 NamedPipe: Client '{client.ClientId}' connected with priority {client.Priority}");

				// ---- determine whether this client should take control ----
				bool shouldControl = TryPromoteClient(client);
				SendControlState(client, shouldControl);

				if (shouldControl)
					ActivateClient(client, keyPressHandler);

				// ---- main read loop ----
				while (client.PipeServer.IsConnected && !_isShuttingDown)
				{
					string command = await client.Reader.ReadLineAsync();
					if (command == null)
					{
						Debug.WriteLine($"⚠️ NamedPipe: Client '{client.ClientId}' disconnected");
						break;
					}

					Debug.WriteLine($"📩 [{client.ClientId}] Received: {command}");
					ProcessCommand(client, command);
				}
			}
			catch (IOException ex)
			{
				Debug.WriteLine($"⚠️ NamedPipe: Client '{client.ClientId}' pipe error: {ex.Message}");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ NamedPipe: Client '{client.ClientId}' error: {ex.Message}");
			}
			finally
			{
				VKey.OnKeyPressedExternal -= keyPressHandler;

				bool wasControlling;
				lock (_clientsLock)
				{
					wasControlling = client.IsControlling;
					client.IsControlling = false;
					_clients.Remove(client);
					if (_controllingClient == client)
						_controllingClient = null;
				}

				client.PipeServer?.Dispose();
				Debug.WriteLine($"🧹 NamedPipe: Client '{client.ClientId}' cleaned up");

				if (wasControlling)
				{
					// Clear SD state and try to promote the next best client
					VPages.ClearAll();
					SDeck.ClearPendingCommands();
					SDeck.ClearVirtualKeyVisuals();

					ConnectedClient next = null;
					lock (_clientsLock)
						next = _clients.OrderByDescending(c => c.Priority).FirstOrDefault();

					if (next != null)
					{
						Debug.WriteLine($"🔼 NamedPipe: Promoting '{next.ClientId}' after controlling client left");
						next.IsControlling = true;
						_controllingClient = next;
						SendControlState(next, true);
						ReplayClientState(next);
						VKey.OnKeyPressedExternal += (kn) => { if (_controllingClient == next) _ = SendKeyPressToClient(next, kn); };
					}
					else
					{
						SDeck.Deactivate();
					}
				}
			}
		}

		// -----------------------------------------------------------------------
		// Control arbitration helpers
		// -----------------------------------------------------------------------

		/// <summary>
		/// Returns true and grants control to <paramref name="candidate"/> if it should
		/// be the controlling client right now.
		/// </summary>
		private static bool TryPromoteClient(ConnectedClient candidate)
		{
			lock (_clientsLock)
			{
				if (_controllingClient == null || candidate.Priority > _controllingClient.Priority)
				{
					if (_controllingClient != null && _controllingClient != candidate)
					{
						// Demote current controller
						var prev = _controllingClient;
						prev.IsControlling = false;
						SendControlState(prev, false);
						VPages.ClearAll();
						SDeck.ClearPendingCommands();
						SDeck.ClearVirtualKeyVisuals();
						Debug.WriteLine($"⬇️ NamedPipe: Demoted '{prev.ClientId}'");
					}

					candidate.IsControlling = true;
					_controllingClient = candidate;
					return true;
				}

				return false;
			}
		}

		/// <summary>
		/// Activates SDeck for the new controlling client, waits for keys, and wires up key press events.
		/// </summary>
		private static void ActivateClient(ConnectedClient client, Action<string> keyPressHandler)
		{
			SDeck.Activate();

			if (!SDeck.AreVirtualKeysReady())
			{
				Debug.WriteLine("⚡ Forcing immediate profile switch for new controller");
				SDeck.ForceImmediateProfileSwitch();

				int waitMs = 0;
				while (!SDeck.AreVirtualKeysReady() && waitMs < 5000)
				{
					Thread.Sleep(100);
					waitMs += 100;
				}

				Debug.WriteLine(SDeck.AreVirtualKeysReady()
					? $"✅ Keys ready after {waitMs}ms"
					: "⚠️ Keys not ready after 5 s — continuing anyway");
			}

			VKey.OnKeyPressedExternal += keyPressHandler;
			ReplayClientState(client);
		}

		/// <summary>
		/// Replays all previously stored key and page definitions for the client,
		/// then restores the last active page.
		/// </summary>
		private static void ReplayClientState(ConnectedClient client)
		{
			Debug.WriteLine($"🔁 Replaying state for '{client.ClientId}': {client.KeyDefs.Count} keys, {client.PageDefs.Count} pages");

			// Replay key definitions
			foreach (var kd in client.KeyDefs.Values)
				HandleDefineKeyForClient(client, kd.Name, kd.Title, kd.IconFileName, sendAck: false);

			// Replay page definitions
			foreach (var pd in client.PageDefs)
				HandleDefinePageForClient(client, pd.Args, sendAck: false);

			// Restore active page
			if (client.CurrentPageName != null)
				HandleSwitchPageForClient(client.CurrentPageName);
		}

		/// <summary>Sends ControlGranted or ControlDenied to the client.</summary>
		private static void SendControlState(ConnectedClient client, bool granted)
		{
			try
			{
				var msg = granted ? "ControlGranted" : "ControlDenied";
				lock (client.WriteLock)
				{
					client.Writer.WriteLine(msg);
					client.Writer.Flush();
				}
				Debug.WriteLine($"📤 [{client.ClientId}] Sent: {msg}");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ Failed to send control state to '{client.ClientId}': {ex.Message}");
			}
		}

		// -----------------------------------------------------------------------
		// Command dispatch
		// -----------------------------------------------------------------------

		private static void ProcessCommand(ConnectedClient client, string command)
		{
			if (string.IsNullOrWhiteSpace(command))
				return;

			try
			{
				var parts = command.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length == 0)
					return;

				var commandName = parts[0].ToUpperInvariant();
				var args = parts.Length > 1 ? parts[1] : string.Empty;

				Debug.WriteLine($"🔧 [{client.ClientId}] Processing command '{commandName}'");

				switch (commandName)
				{
					case "DEFINEKEY":
						HandleDefineKeyCommand(client, args);
						break;

					case "DEFINEPAGE":
						HandleDefinePageCommand(client, args);
						break;

					case "SETKEYVISUALS":
						if (client.IsControlling)
							HandleSetKeyVisuals(client, args);
						break;

					case "SWITCHPAGE":
						if (client.IsControlling)
						{
							client.CurrentPageName = args.Trim();
							HandleSwitchPageForClient(client.CurrentPageName);
						}
						break;

					case "SWITCHPROFILE":
						if (client.IsControlling)
							HandleSwitchProfile(args);
						break;

					case "SWITCHPROFILEBACK":
						if (client.IsControlling)
							HandleSwitchProfileBack();
						break;

					default:
						Debug.WriteLine($"⚠️ [{client.ClientId}] Unknown command '{commandName}'");
						break;
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ [{client.ClientId}] Error processing command '{command}': {ex.Message}");
			}
		}

		// -----------------------------------------------------------------------
		// DefineKey — stored per-client; applied to global state only when in control
		// -----------------------------------------------------------------------

		private static void HandleDefineKeyCommand(ConnectedClient client, string args)
		{
			var parts = ParseCommandArgs(args, 3);

			if (parts.Length < 1)
			{
				Debug.WriteLine("⚠️ NamedPipe: DefineKey requires: KeyName [Title] [IconFileName]");
				_ = SendEventToClient(client, $"KeyDefined unknown ERROR Missing required parameter: KeyName required");
				return;
			}

			string keyName     = parts[0];
			string title       = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) && parts[1] != "null" ? parts[1] : null;
			string iconFileName = parts.Length > 2 && !string.IsNullOrEmpty(parts[2]) && parts[2] != "null" ? parts[2] : null;

			// Always store in per-client registry
			client.KeyDefs[keyName] = new KeyDefinition { Name = keyName, Title = title, IconFileName = iconFileName };

			// Apply to global SD state only if this client is in control
			if (client.IsControlling)
				HandleDefineKeyForClient(client, keyName, title, iconFileName, sendAck: true);
			else
				_ = SendEventToClient(client, $"KeyDefined {keyName} OK");
		}

		private static void HandleDefineKeyForClient(ConnectedClient client, string keyName, string title, string iconFileName, bool sendAck)
		{
			try
			{
				var keyDef = new KeyDefinition
				{
					Name          = keyName,
					IconFileName  = iconFileName,
					Title         = title
							};

							// Global key definitions used only while this client is controlling
							// Stored on the client; passed in during replay
							_keyDefinitions_forClient(client)[keyDef.Name] = keyDef;
							if (sendAck)
								_ = SendEventToClient(client, $"KeyDefined {keyName} OK");

							if (iconFileName == null && title == null)
								Debug.WriteLine($"✅ [{client.ClientId}] Defined blank key '{keyName}'");
							else if (iconFileName == null)
								Debug.WriteLine($"✅ [{client.ClientId}] Defined title-only key '{keyName}' title='{title}'");
							else if (title == null)
								Debug.WriteLine($"✅ [{client.ClientId}] Defined icon-only key '{keyName}' icon='{iconFileName}'");
							else
								Debug.WriteLine($"✅ [{client.ClientId}] Defined key '{keyName}' title='{title}' icon='{iconFileName}'");
						}
						catch (Exception ex)
						{
							Debug.WriteLine($"❌ [{client.ClientId}] Error defining key '{keyName}': {ex.Message}");
							if (sendAck)
								_ = SendEventToClient(client, $"KeyDefined {keyName} ERROR {ex.Message}");
						}
					}

					// This is a per-client in-memory key store for building pages.
					// When in control we write directly into client.KeyDefs (already done above),
					// but HandleDefinePage needs to look keys up by name at page-build time.
					// A tiny helper keeps the lookup consistent.
					private static Dictionary<string, KeyDefinition> _keyDefinitions_forClient(ConnectedClient client)
						=> client.KeyDefs;

					// -----------------------------------------------------------------------
					// DefinePage — stored per-client; built against global VPages when in control
					// -----------------------------------------------------------------------

					private static void HandleDefinePageCommand(ConnectedClient client, string args)
					{
						// Always persist the raw definition so it can be replayed
						client.PageDefs.Add(new SavedPageDef { Args = args });

						if (client.IsControlling)
							HandleDefinePageForClient(client, args, sendAck: true);
						else
						{
							// Extract page name for the ack
							var pageName = args.Split(new[] { ' ' }, 2)[0].Split(':')[0];
							_ = SendEventToClient(client, $"PageDefined {pageName} OK");
						}
					}

					/// <summary>
					/// Handle DefinePage command.
					/// Format: "DefinePage PageName[:BasePage] [[key00,key01,key02],...] "
					/// Supports inheritance: DefinePage ChildPage:ParentPage [...]
					/// Grid semantics: null = inherit from parent, "" = clear position, "KeyName" = use specific key
					/// </summary>
					private static void HandleDefinePageForClient(ConnectedClient client, string args, bool sendAck)
					{
						var parts = args.Split(new[] { ' ' }, 2);
						if (parts.Length < 2)
						{
							Debug.WriteLine("⚠️ NamedPipe: DefinePage requires: PageName[:BasePage] KeyGrid");
							if (sendAck)
								_ = SendEventToClient(client, "PageDefined unknown ERROR Missing required parameters: PageName and KeyGrid required");
							return;
						}

						string pageNameFull = parts[0];
						string keyGridJson  = parts[1];

						string pageName;
						string basePageName = null;

						int colonIndex = pageNameFull.IndexOf(':');
						if (colonIndex > 0)
						{
							pageName     = pageNameFull.Substring(0, colonIndex);
							basePageName = pageNameFull.Substring(colonIndex + 1);

							if (string.IsNullOrWhiteSpace(basePageName))
							{
								Debug.WriteLine($"⚠️ NamedPipe: Invalid inheritance syntax for '{pageNameFull}'");
								if (sendAck)
									_ = SendEventToClient(client, $"PageDefined {pageName} ERROR Invalid inheritance syntax: base page name cannot be empty after ':'");
								return;
							}
							Debug.WriteLine($"🔗 [{client.ClientId}] Creating page '{pageName}' inheriting from '{basePageName}'");
						}
						else
						{
							pageName = pageNameFull;
							Debug.WriteLine($"📄 [{client.ClientId}] Creating page '{pageName}'");
						}

						try
						{
							var childGrid = ParseKeyGrid(keyGridJson);
							if (childGrid == null)
							{
								if (sendAck)
									_ = SendEventToClient(client, $"PageDefined {pageName} ERROR Invalid JSON grid format");
								return;
							}

							VPage basePage = null;
							if (basePageName != null)
							{
								basePage = VPages.GetByName(basePageName);
								if (basePage == null)
								{
									if (sendAck)
										_ = SendEventToClient(client, $"PageDefined {pageName} ERROR Base page '{basePageName}' not defined");
									return;
								}
							}

							var keyDefs  = _keyDefinitions_forClient(client);
							var finalGrid = new string[5, 3];
							var missingKeys = new List<string>();

							for (int r = 0; r < 5; r++)
							{
								for (int c = 0; c < 3; c++)
								{
									string childKey = childGrid[r, c];

									if (childKey == null)
									{
										if (basePage != null && basePage.VKeys[r, c] != null)
										{
											var baseVKey  = basePage.VKeys[r, c];
											var nameField = baseVKey.GetType().GetField("_name", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
											finalGrid[r, c] = nameField?.GetValue(baseVKey) as string;
										}
										else
										{
											finalGrid[r, c] = null;
											if (basePage == null)
												missingKeys.Add($"null@({r},{c})-no_base_page");
										}
									}
									else if (childKey == string.Empty)
									{
										finalGrid[r, c] = null;
									}
									else
									{
										finalGrid[r, c] = childKey;
										if (!keyDefs.ContainsKey(childKey))
											missingKeys.Add($"{childKey}@({r},{c})");
									}
								}
							}

							if (missingKeys.Count > 0)
							{
								var errorMsg = $"Undefined keys: {string.Join(", ", missingKeys)}";
								Debug.WriteLine($"❌ [{client.ClientId}] Page '{pageName}' failed - {errorMsg}");
								if (sendAck)
									_ = SendEventToClient(client, $"PageDefined {pageName} ERROR {errorMsg}");
								return;
							}

							var vPage = new VPage(pageName, rows: 5, cols: 3);

							for (int r = 0; r < 5; r++)
							{
								for (int c = 0; c < 3; c++)
								{
									string keyName = finalGrid[r, c];
									if (!string.IsNullOrEmpty(keyName))
									{
										var kd = keyDefs[keyName];
										new VKey(
											name:     kd.Name,
											vPage:    vPage,
											row:      r,
											col:      c,
											onPress:  () => { /* handled by OnKeyPressedExternal */ },
											minPress: 0,
											image:    kd.IconFileName,
											title:    kd.Title
										);
									}
								}
							}

							if (sendAck)
								_ = SendEventToClient(client, $"PageDefined {pageName} OK");

							Debug.WriteLine(basePageName != null
								? $"✅ [{client.ClientId}] Created page '{pageName}' inheriting from '{basePageName}'"
								: $"✅ [{client.ClientId}] Created page '{pageName}'");
						}
						catch (Exception ex)
						{
							Debug.WriteLine($"❌ [{client.ClientId}] Error creating page '{pageName}': {ex.Message}");
							if (sendAck)
								_ = SendEventToClient(client, $"PageDefined {pageName} ERROR {ex.Message}");
						}
					}

					// -----------------------------------------------------------------------
					// SwitchPage / SwitchProfile / SwitchProfileBack
					// -----------------------------------------------------------------------

					private static void HandleSwitchPageForClient(string pageName)
					{
						if (string.IsNullOrWhiteSpace(pageName))
						{
							Debug.WriteLine("⚠️ NamedPipe: SwitchPage requires a page name");
							return;
						}

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

					private static void HandleSwitchProfileBack()
					{
						Debug.WriteLine("🔙 NamedPipe: Switching back to previous profile");
						SDeck.SwitchBackToPreviousProfile();
					}

					// -----------------------------------------------------------------------
					// SetKeyVisuals
					// -----------------------------------------------------------------------

					/// <summary>
					/// Handle SetKeyVisuals command from Content Manager
					/// Format: "SetKeyVisuals KeyName [Title] [IconFileName]"
					/// Note: Updates only the key in the currently active page, not the shared definition
					/// </summary>
					private static void HandleSetKeyVisuals(ConnectedClient client, string args)
					{
						var parts = ParseCommandArgs(args, 3);

						if (parts.Length < 1)
						{
							Debug.WriteLine("⚠️ NamedPipe: SetKeyVisuals requires: KeyName [Title] [IconFileName]");
							_ = SendEventToClient(client, "KeyVisualsSet unknown ERROR Missing required parameter: KeyName required");
							return;
						}

						string keyName = parts[0];

						try
						{
							if (!client.KeyDefs.ContainsKey(keyName))
							{
								_ = SendEventToClient(client, $"KeyVisualsSet {keyName} ERROR Key '{keyName}' not defined");
								return;
							}

							var currentPage = SDeck.SDPage?.VPage;
							if (currentPage == null)
							{
								_ = SendEventToClient(client, $"KeyVisualsSet {keyName} ERROR No active page");
								return;
							}

							VKey targetKey = null;
							int  targetRow = -1, targetCol = -1;

							for (int r = 0; r < currentPage.Rows; r++)
							{
								for (int c = 0; c < currentPage.Cols; c++)
								{
									var vKey = currentPage.VKeys[r, c];
									if (vKey != null)
									{
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
								_ = SendEventToClient(client, $"KeyVisualsSet {keyName} ERROR Key '{keyName}' not found in current page");
								return;
							}

							string newTitle = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) && parts[1] != "null" ? parts[1] : null;
							string newIcon  = parts.Length > 2 && !string.IsNullOrEmpty(parts[2]) && parts[2] != "null" ? parts[2] : null;

							new VKey(
								name:     keyName,
								vPage:    currentPage,
								row:      targetRow,
								col:      targetCol,
								onPress:  () => { /* handled by OnKeyPressedExternal */ },
								minPress: 0,
								image:    newIcon,
								title:    newTitle
							);

							_ = SendEventToClient(client, $"KeyVisualsSet {keyName} OK");
							Debug.WriteLine($"✅ [{client.ClientId}] Updated visuals for key '{keyName}' - Title: '{newTitle ?? "null"}', Icon: '{newIcon ?? "null"}'");
						}
						catch (Exception ex)
						{
							Debug.WriteLine($"❌ [{client.ClientId}] Error setting visuals for key '{keyName}': {ex.Message}");
							_ = SendEventToClient(client, $"KeyVisualsSet {keyName} ERROR {ex.Message}");
						}
					}

					// -----------------------------------------------------------------------
					// Generic event sender + key press helper
					// -----------------------------------------------------------------------

					private static Task SendEventToClient(ConnectedClient client, string message)
					{
						try
						{
							if (!client.PipeServer.IsConnected)
								return Task.CompletedTask;

							lock (client.WriteLock)
							{
								client.Writer.WriteLine(message);
								client.Writer.Flush();
							}
							Debug.WriteLine($"📤 [{client.ClientId}] Sent: {message}");
						}
						catch (Exception ex)
						{
							Debug.WriteLine($"❌ [{client.ClientId}] Error sending '{message}': {ex.Message}");
						}
						return Task.CompletedTask;
					}

					private static Task SendKeyPressToClient(ConnectedClient client, string keyName)
						=> SendEventToClient(client, $"KeyPress {keyName}");

					// -----------------------------------------------------------------------
					// Parsing utilities (unchanged)
					// -----------------------------------------------------------------------

					/// <summary>
					/// Parse command arguments, respecting quoted strings
					/// Example: "key1 icon.png \"My Title\"" → ["key1", "icon.png", "My Title"]
					/// </summary>
					private static string[] ParseCommandArgs(string args, int maxParts)
					{
						var result   = new List<string>();
						bool inQuotes = false;
						var current  = new StringBuilder();

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
							var grid = new string[5, 3];

							for (int r = 0; r < Math.Min(5, rows.Length); r++)
								for (int c = 0; c < Math.Min(3, rows[r].Length); c++)
									grid[r, c] = rows[r][c];

							return grid;
						}
						catch (Exception ex)
						{
							Debug.WriteLine($"❌ NamedPipe: Failed to parse key grid: {ex.Message}");
							return null;
						}
					}
					}
				}
