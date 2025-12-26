using streamdeck_client_csharp;
using streamdeck_client_csharp.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace NWRS_AC_SDPlugin
{
	// It would be nice to have a map of these so that
	// base64 images do not have a ton of copies
	public class SDImage
	{
		private static readonly Dictionary<string, string> _imageCache = new Dictionary<string, string>();
		private static string _blankImage = null;
		private static string _blackImage = null;
		private static Color CompanyBlue = Color.FromArgb(0x10, 0x2f, 0x45);
		private static Color CompanyYellow = Color.FromArgb(0xf5, 0xb9, 0x41);

		public static string BlackImage()
		{
			if (_blackImage is null) {
				_blackImage = CreateBlankImage(Color.Black).GetImage();
			}
			return _blackImage;
		}

		public static string BlankImage()
		{
			if (_blankImage is null) {
				_blankImage = CreateBlankImage(CompanyBlue).GetImage();
			}
			return _blankImage;
		}

		private readonly string _image;

		public static SDImage CreateBlankImage(Color color)
		{
			Bitmap blackImage = new Bitmap(72, 72);
			using (Graphics gfx = Graphics.FromImage(blackImage)) {
				gfx.Clear(color);
			}
			return new SDImage(blackImage);
		}

		public SDImage(string image, bool inverted = false)
		{
			if (image.StartsWith("data:image/png;base64,")) {
				// Direct base64 image
				_image = image;
			} else if (image.StartsWith("!")) {
				// Text-based icon: "!TextHere"
				_image = CreateBase64PngFromText(image[1..], inverted);
				_imageCache[image] = _image;
			} else if (Path.IsPathRooted(image)) {
				// Absolute file path
				string imageName = $"{image}{(inverted ? "-inv" : "")}";
				if (!_imageCache.TryGetValue(imageName, out _image)) {
					try {
						byte[] imageBytes = File.ReadAllBytes(image);
						_image = "data:image/png;base64," + Convert.ToBase64String(imageBytes);
						_imageCache[imageName] = _image;
					} catch (Exception ex) {
						Debug.WriteLine($"Failed to read image from absolute path {image}: {ex.Message}");
						_image = CreateBase64PngFromText(Path.GetFileNameWithoutExtension(image), inverted);
						_imageCache[imageName] = _image;
					}
				}
			} else {
				// Relative path - search in assets folder (legacy support)
				int startIndex = image.IndexOf('_') + 1;
				if (startIndex > 0)
					image = image[startIndex..];
				int endIndex = image.LastIndexOf('.');
				if (endIndex > 0) {
				image = image[..endIndex];
				}

				string imageName = $"{image}{(inverted ? "-inv" : "")}";
				if (!_imageCache.TryGetValue(imageName, out _image)) {
					try {
						byte[] imageBytes = File.ReadAllBytes($"assets\\SD-Icons\\{imageName}.png");
						_image = "data:image/png;base64," + Convert.ToBase64String(imageBytes);
					} catch (Exception ex) {
						Debug.WriteLine($"Failed to read image {image}: {ex.Message}");
						_image = CreateBase64PngFromText(image, inverted);
					}
					_imageCache[imageName] = _image;
				}
			}
		}

		public SDImage(Bitmap imageBitmap, int size = 72, bool crop = true)
		{
			int squareDimension;
			int x, y;
			if (crop) {
				// Step 1: Crop the bitmap from the longer dimension
				squareDimension = Math.Min(imageBitmap.Width, imageBitmap.Height);
			} else {
				// Step 1: Make the bitmap square by adding black stripes
				squareDimension = Math.Max(imageBitmap.Width, imageBitmap.Height);
			}
			x = (squareDimension - imageBitmap.Width) / 2;
			y = (squareDimension - imageBitmap.Height) / 2;

			Bitmap squareBitmap = new Bitmap(squareDimension, squareDimension);
			using (Graphics graphics = Graphics.FromImage(squareBitmap)) {
				graphics.Clear(Color.Black);
				graphics.DrawImage(imageBitmap, x, y, imageBitmap.Width, imageBitmap.Height);
			}

			// Step 2: Resize the square bitmap to the desired size (72x72 pixels)
			Bitmap resizedBitmap = new Bitmap(size, size);

			using (Graphics graphics = Graphics.FromImage(resizedBitmap)) {
				graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
				graphics.DrawImage(squareBitmap, 0, 0, size, size);
			}

			using var memoryStream = new MemoryStream();
			resizedBitmap.Save(memoryStream, ImageFormat.Png);
			byte[] imageBytes = memoryStream.ToArray();
			_image = "data:image/png;base64," + Convert.ToBase64String(imageBytes);
		}

		public string GetImage()
		{
			return _image;
		}

		static string CreateBase64PngFromText(string text, bool inverted = false)
		{
			var bg = inverted ? CompanyYellow : CompanyBlue;
			var fg = inverted ? CompanyBlue : CompanyYellow;

			// Define the font and size
			var font = new Font("Arial", 6, FontStyle.Bold);

			// Create a new bitmap with the desired size (72x72 pixels)
			using var bitmap = new Bitmap(72, 72);
			using var graphics = Graphics.FromImage(bitmap);

			// SetIfNull the background color to white
			graphics.Clear(bg);

			// Measure the size of the text
			SizeF textSize = graphics.MeasureString(text, font);

			// Calculate the position to center the text
			float x = (bitmap.Width - textSize.Width) / 2;
			float y = (bitmap.Height - textSize.Height) / 2;

			// Draw the text on the bitmap
			using (Brush textBrush = new SolidBrush(fg)) {
				graphics.DrawString(text, font, textBrush, x, y);
			}

			// Save the bitmap to a MemoryStream
			using var memoryStream = new MemoryStream();
			
			bitmap.Save(memoryStream, ImageFormat.Png);
			byte[] imageBytes = memoryStream.ToArray();

			// Convert the byte array to a base64 string
			string base64String = Convert.ToBase64String(imageBytes);

			// Format the base64 string as a data URI
			return $"data:image/png;base64,{base64String}";
		}
	}

	public class SDKey(SDPage sdPage, int row, int col)
	{
		private readonly SDPage _sdPage = sdPage;
		public readonly int Row = row;
		public readonly int Col = col;
		public string Context = null;
		public int KeyDownFor => _keyDownTime < DateTime.Now ? (int)(DateTime.Now - _keyDownTime).TotalMilliseconds : 0;
		private bool _staleImage = false;
		private bool _staleTitle = false;
		private VKey _vKey => _sdPage.VPage?.VKeys[Row, Col];
		private DateTime _keyDownTime = DateTime.MaxValue;

		public void SetContext(string context)
		{
			Context = context;
			Reset();
		}

		public void Reset()
		{
			// Send the vkey to the Stream Deck
			_staleImage = true;
			_staleTitle = true;
			Refresh();
		}

		public void NewImage()
		{
			_staleImage = true;
			Refresh();
		}

		public void NewTitle()
		{
			_staleTitle = true;
			Refresh();
		}

		public void Refresh()
		{
			// Always refresh if we have a context and connection - SDeck will handle when to actually send
			if (Context != null && SDeck.Conn != null) {
				if (_staleImage) {
					var image = _vKey?.GetImage();
					// Log image setting without the massive base64 data
					Debug.WriteLineIf(image is not null, $"Setting image for {Context} - image length: {image?.Length ?? 0} chars");
					SDeck.Conn.SetImageAsync(_vKey?.GetImage(), Context, SDKTarget.HardwareAndSoftware, null);
					_staleImage = false;
				}
				if (_staleTitle) {
					SDeck.Conn.SetTitleAsync((_vKey?.GetTitle()), Context, SDKTarget.HardwareAndSoftware, null);
					_staleTitle = false;
				}
			} else {
				// Keep stale flags set if we can't refresh right now
				_staleImage = true;
				_staleTitle = true;
			}
		}

		public void OnKeyDown()
		{
			_keyDownTime = DateTime.Now;
		}

		public void OnKeyUp()
		{
			var duration = (int)(DateTime.Now - _keyDownTime).TotalMilliseconds;
			_keyDownTime = DateTime.MaxValue;
			_vKey?.OnKeyPress(duration);
		}

		public void OnKeyCancel()
		{
			_keyDownTime = DateTime.MaxValue;
		}
	}

	class BackKey(string context)
	{
		private readonly string _context = context;
		private string _returnContext = null;

		public string GetContext()
		{
			return _context;
		}	

		public void SetReturnContext(string returnContext)
		{
			_returnContext = returnContext;
		}
	}

	// This class represents a fully managed Stream Deck Page
	public class SDPage
	{
		public SDKey[,] SDKeys { get; set; }
		public int Rows;
		public int Cols;
		public VPage VPage { get; private set; } = null;
		private Dictionary<string, SDKey> _sdKeyMap = new Dictionary<string, SDKey>();
		private SDKey _debugKey1;
		private string _debugKey2Context;
		private int _debugKey1MinPress = 1000;

		public SDPage(int rows = 5, int cols = 3)
		{
			Rows = rows;
			Cols = cols;
			SDKeys = new SDKey[Rows, Cols];
			for (int row = 0; row < Rows; row++) {
				for (int col = 0; col < Cols; col++) {
					SDKeys[row, col] = new SDKey(this, row, col);
				}
			}
		}

		public void SetVPage(VPage page)
		{
			lock (this) {
				VPage = page;

				for (int row = 0; row < Rows; row++) {
					for (int col = 0; col < Cols; col++) {
						SDKeys[row, col].Reset();
					}
				}
			}
		}

		public void NewVKey(VPage vPage, int row, int col)
		{
			lock (this) {
				if (vPage == VPage) {
					SDKeys[row, col].Reset();
				}
			}
		}

		public void NewKeyImage(VPage vPage, int row, int col)
		{
			lock (this) {
				if (vPage == VPage) {
					SDKeys[row, col].NewImage();
				}
			}
		}

		public void NewKeyTitle(VPage vPage, int row, int col)
		{
			lock (this) {
				if (vPage == VPage) {
					SDKeys[row, col].NewTitle();
				}
			}
		}

		// Helper function to get the key position (row, col) from the Stream Deck coordinates
		public (int, int) GetKeyPosition(Coordinates coord)
		{
			// return (SDKeys.GetLength(0) - coord.Columns - 1, coord.Rows);
			return (coord.Columns, SDKeys.GetLength(1) - coord.Rows - 1);
		}

		public void AddKey(Coordinates coord, string context)
		{
			(int row, int col) = GetKeyPosition(coord);
			if (row == 0 && col == 0) {
				_debugKey1 = SDKeys[row, col];	
			}
			if (row == 0 && col == 2) {
				_debugKey2Context = context;
			}

			Debug.Assert(SDKeys[row, col] is not null);
			Debug.Assert(SDKeys[row, col].Context == null || SDKeys[row, col].Context == context);

			SDKeys[row, col].SetContext(context);
			_sdKeyMap[context] = SDKeys[row, col];
		}

		public void RemoveKey(Coordinates coord, string context)
		{
			(int row, int col) = GetKeyPosition(coord);

			Debug.Assert(SDKeys[row, col] is not null);
			Debug.Assert(SDKeys[row, col].Context == context);
			// Don't do anything since context never changes
			// This only means that the SDKey is not on display which we don't care
		}

		private void onKey(Coordinates coord, string context, Action<SDKey> action)
		{
			if (_sdKeyMap.TryGetValue(context, out SDKey sdKey)) {
				Debug.Assert(sdKey is not null);
				Debug.Assert(sdKey.Context == context);
				Debug.Assert((GetKeyPosition(coord) is var (row, col)) && SDKeys[row, col] == sdKey);

				action.Invoke(sdKey);
			} else {
				Debug.WriteLine($"Key {context} not found");
			}
		}

		public void OnKeyDown(Coordinates coord, string context)
		{
			onKey(coord, context, sdKey => sdKey.OnKeyDown());
		}

		public void OnKeyUp(Coordinates coord, string context)
		{
			if (context == _debugKey2Context && _debugKey1.KeyDownFor > _debugKey1MinPress) {
				_debugKey1.OnKeyCancel();
				// UIAutomation.CA.DBGDisplayTree();
			} else {
				onKey(coord, context, sdKey => sdKey.OnKeyUp());
			}
		}
	}

	/// <summary>
	/// Simplified StreamDeck manager with stateful interface
	/// Remembers what you want and executes it when possible, regardless of connection state
	/// </summary>
	public static class SDeck
	{
		// Profile name constant
		private const string PROFILE_NAME = "NWRS AC";
		
		// Connection management
		private static StreamDeckConnection _conn;
		public static StreamDeckConnection Conn;
		private static string _pluginUUID = null;
		private static string _deviceID = null;
		private static bool _isShuttingDown = false;
		
		// State management
		private static string _currentProfile = null;
		private static string _desiredProfile = null;
		private static readonly Stack<string> _profileStack = new Stack<string>();
		private static VPage _currentVPage = null;
		private static VPage _desiredVPage = null;
		
		// Threading and connection
		private static readonly object _stateLock = new object();
		private static Timer _commandTimer;
		private static readonly Queue<Action> _pendingCommands = new Queue<Action>();
		
		// Hardware interface
		public static SDPage SDPage { get; private set; }
		private static BackKey _backKey = null;
		
		// Connection status tracking
		private static bool _deviceConnected = false;
		private static bool _virtualKeysReady = false;
		private static DateTime _lastProfileSwitchToNWRS = DateTime.MinValue;
		
		// Active state - only manage profiles when active
		private static bool _isActive = false;
		
		/// <summary>
		/// Initialize the StreamDeck manager with connection parameters from command line
		/// Does NOT switch profiles or activate - waits for Activate() call
		/// </summary>
		public static void Init(int port, string pluginUUID, string registerEvent)
		{
			Debug.WriteLine($"🔧 SDeck: Initializing with port={port}, uuid={pluginUUID}, event={registerEvent}");
			
			lock (_stateLock)
			{
				// Start in passive mode - don't assume any profile
				_currentProfile = null;
				_desiredProfile = null;
				_profileStack.Clear();
				_pendingCommands.Clear();
				_isActive = false;
			}
			
			SDPage = new SDPage();
			
			// Connect immediately with provided parameters
			try
			{
				EstablishStreamDeckConnection(port, pluginUUID, registerEvent);
				StartCommandProcessor();
				Debug.WriteLine("✅ SDeck: Initialization complete - in passive mode");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ SDeck: Initialization failed: {ex.Message}");
				Environment.Exit(1);
			}
		}
		
		/// <summary>
		/// Activate SDeck profile management
		/// Call this when your application wants to take control of the StreamDeck
		/// Switches to the configured profile and starts managing it
		/// </summary>
		public static void Activate()
		{
			lock (_stateLock)
			{
				if (_isActive)
				{
					Debug.WriteLine("⚠️ SDeck.Activate: Already active");
					return;
				}
				
				Debug.WriteLine("✅ SDeck: Activating profile management");
				
				_isActive = true;
				_desiredProfile = PROFILE_NAME;
				
				// Initialize profile stack with NWRS AC as base
				_profileStack.Clear();
				_profileStack.Push(PROFILE_NAME);
				
				// Switch to NWRS AC profile
				Debug.WriteLine($"🔄 SDeck: Switching to {PROFILE_NAME}");
				_pendingCommands.Enqueue(() => ExecuteProfileSwitch(PROFILE_NAME));
			}
		}
		
		/// <summary>
		/// Deactivate SDeck profile management
		/// Call this when your application no longer needs control of the StreamDeck
		/// Stops managing profiles but doesn't switch away (user might have manually switched)
		/// </summary>
		public static void Deactivate()
		{
			lock (_stateLock)
			{
				if (!_isActive)
				{
					Debug.WriteLine("⚠️ SDeck.Deactivate: Already inactive");
					return;
				}
				
				Debug.WriteLine("🛑 SDeck: Deactivating profile management");
				
				_isActive = false;
				_desiredProfile = null;
				_profileStack.Clear();
				
				Debug.WriteLine("ℹ️ SDeck: Entering passive mode - no longer managing profiles");
			}
		}
		
		/// <summary>
		/// Check if SDeck is currently active and managing profiles
		/// </summary>
		public static bool IsActive()
		{
			lock (_stateLock)
			{
				return _isActive;
			}
		}
		
		/// <summary>
		/// Shutdown the StreamDeck manager
		/// </summary>
		public static void Shutdown()
		{
			Debug.WriteLine("🔧 SDeck: Shutting down StreamDeck manager");
			_isShuttingDown = true;
			
			_commandTimer?.Dispose();
			_conn?.Stop();
			
			Environment.Exit(0);
		}
		
		/// <summary>
		/// Switch to a profile and remember the current one
		/// Only works when SDeck is active
		/// </summary>
		public static void SwitchToProfile(string profileName)
		{
			if (string.IsNullOrEmpty(profileName))
			{
				Debug.WriteLine("❌ SDeck.SwitchToProfile: Invalid profile name");
				return;
			}
			
			lock (_stateLock)
			{
				if (!_isActive)
				{
					Debug.WriteLine($"⚠️ SDeck.SwitchToProfile: Ignoring request (SDeck not active)");
					return;
				}
				
				Debug.WriteLine($"🔄 SDeck.SwitchToProfile: {_currentProfile} -> {profileName}");
				
				// Remember current profile before switching
				if (_currentProfile != profileName)
				{
					_profileStack.Push(_currentProfile);
					Debug.WriteLine($"📚 SDeck: Profile stack now has {_profileStack.Count} items, top: {_profileStack.Peek()}");
				}
				
				_desiredProfile = profileName;
				
				// Queue the command for execution
				_pendingCommands.Enqueue(() => ExecuteProfileSwitch(profileName));
			}
		}
		
		/// <summary>
		/// Switch back to the previous profile
		/// Only works when SDeck is active
		/// </summary>
		public static void SwitchBackToPreviousProfile()
		{
			lock (_stateLock)
			{
				if (!_isActive)
				{
					Debug.WriteLine($"⚠️ SDeck.SwitchBackToPreviousProfile: Ignoring request (SDeck not active)");
					return;
				}
				
				if (_profileStack.Count <= 1)
				{
					Debug.WriteLine("⚠️ SDeck.SwitchBackToPreviousProfile: No previous profile to return to");
					return;
				}
				
				// Pop current profile and get previous
				_profileStack.Pop();
				var previousProfile = _profileStack.Peek();
				
				Debug.WriteLine($"🔙 SDeck.SwitchBackToPreviousProfile: {_currentProfile} -> {previousProfile}");
				Debug.WriteLine($"📚 SDeck: Profile stack now has {_profileStack.Count} items, top: {previousProfile}");
				
				_desiredProfile = previousProfile;
				
				// Queue the command for execution
				_pendingCommands.Enqueue(() => ExecuteProfileSwitch(previousProfile));
			}
		}
		
		/// <summary>
		/// Set the VPage for NWRS profile
		/// This always succeeds - execution happens when connection is available and in NWRS profile
		/// </summary>
		public static void SetVPage(VPage vPage)
		{
			lock (_stateLock)
			{
				Debug.WriteLine($"🔄 SDeck.SetVPage: {vPage?.Name ?? "null"}");
				_desiredVPage = vPage;
				
				// Queue the command for execution
				_pendingCommands.Enqueue(() => ExecuteVPageSet(vPage));
			}
		}
		
		/// <summary>
		/// Get debug status for troubleshooting
		/// </summary>
		public static string GetStatus()
		{
			lock (_stateLock)
			{
				var connectionState = GetConnectionStateDescription();
				
				return $"SDeck Status: " +
					   $"State={connectionState}, " +
					   $"Connected={_deviceConnected}, " +
					   $"VKeys={_virtualKeysReady}, " +
					   $"Profile={_currentProfile}/{_desiredProfile}, " +
					   $"Stack={_profileStack.Count}, " +
					   $"Pending={_pendingCommands.Count}, " +
					   $"Active={_isActive}";
			}
		}
		
		/// <summary>
		/// Get a human-readable description of the current connection state
		/// This helps distinguish between different types of connection issues
		/// </summary>
		private static string GetConnectionStateDescription()
		{
			if (!_deviceConnected || _conn == null || _deviceID == null)
				return "Disconnected";
			
			if (_currentProfile == PROFILE_NAME && _virtualKeysReady)
				return "Ready";
			
			if (_currentProfile == PROFILE_NAME && !_virtualKeysReady)
				return "NoKeys";
			
			if (_currentProfile != PROFILE_NAME)
				return $"Profile-{_currentProfile}";
			
			return "Connected-Unknown";
		}
		
		/// <summary>
		/// Check if StreamDeck is connected and functional
		/// </summary>
		public static bool IsConnectedAndFunctional()
		{
			lock (_stateLock)
			{
				return _deviceConnected && _conn != null && _deviceID != null;
			}
		}
		
		/// <summary>
		/// Command processor that executes queued commands when connection is ready
		/// </summary>
		private static void StartCommandProcessor()
		{
			_commandTimer = new Timer(ProcessCommands, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
		}
		
		/// <summary>
		/// Processes queued commands when connection is ready
		/// </summary>
		private static void ProcessCommands(object state)
		{
			if (_isShuttingDown) return;
			
			try
			{
				lock (_stateLock)
				{
					// Enhanced connection state monitoring: Check for profile mismatch
					CheckAndCorrectProfileMismatch();
					
					// Only process commands if we have a working connection
					if (!IsReadyForCommands())
					{
						if (_pendingCommands.Count > 0)
						{
							Debug.WriteLine($"⏳ SDeck: {_pendingCommands.Count} commands pending, waiting for connection. Connected={_deviceConnected}, DeviceID={_deviceID != null}");
						}
						return;
					}
					
					// Process all pending commands
					var commandsProcessed = 0;
					while (_pendingCommands.Count > 0 && commandsProcessed < 10)
					{
						var command = _pendingCommands.Dequeue();
						try
						{
							Debug.WriteLine($"🔄 SDeck: Processing command #{commandsProcessed + 1}");
							command.Invoke();
							commandsProcessed++;
						}
						catch (Exception ex)
						{
							Debug.WriteLine($"❌ SDeck.ProcessCommands: Error executing command: {ex.Message}");
						}
					}
					
					if (commandsProcessed > 0)
					{
						Debug.WriteLine($"✅ SDeck: Processed {commandsProcessed} commands");
					}
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ SDeck.ProcessCommands: {ex.Message}");
			}
		}
		
		/// <summary>
		/// Smart profile recovery: Detects when StreamDeck is connected but not showing NWRS virtual keys
		/// and automatically switches to NWRS profile
		/// Only enforces when SDeck is active
		/// </summary>
		private static void CheckAndCorrectProfileMismatch()
		{
			// Only enforce profile when active
			if (!_isActive)
			{
				return; // Passive mode - don't enforce
			}
			
			// Only check if we have a device connection and it's not shutting down
			if (!_deviceConnected || _isShuttingDown || _conn == null || _deviceID == null)
				return;
			
			// Check if we think we should be in NWRS profile but virtual keys aren't ready
			bool shouldBeInNWRS = (_desiredProfile == PROFILE_NAME || _currentProfile == PROFILE_NAME);
			bool hasVirtualKeys = _virtualKeysReady;
			
			if (shouldBeInNWRS && !hasVirtualKeys)
			{
				var timeSinceLastNWRSSwitch = DateTime.Now - _lastProfileSwitchToNWRS;
				
				// Only try recovery for 30 seconds, then give up
				if (timeSinceLastNWRSSwitch < TimeSpan.FromSeconds(30))
				{
					// Wait 5 seconds between recovery attempts (increased from 3)
					bool enoughTimeHasPassed = timeSinceLastNWRSSwitch > TimeSpan.FromSeconds(5);
					
					if (enoughTimeHasPassed)
					{
						Debug.WriteLine($"🔧 SDeck: Virtual keys not ready after {timeSinceLastNWRSSwitch.TotalSeconds:F1}s, attempting recovery");
						_pendingCommands.Enqueue(() => ExecuteProfileSwitch(PROFILE_NAME));
					}
				}
				else
				{
					// After 30 seconds, log a single warning and stop trying
					if (timeSinceLastNWRSSwitch < TimeSpan.FromSeconds(31))
					{
						Debug.WriteLine($"⚠️ SDeck: Virtual keys did not appear after 30 seconds");
						Debug.WriteLine($"ℹ️ SDeck: This is expected if the '{PROFILE_NAME}' profile doesn't have plugin keys configured");
						Debug.WriteLine($"ℹ️ SDeck: Plugin will continue to function for profile switching and other features");
					}
				}
			}
		}
		
		/// <summary>
		/// Check if the connection is ready for commands
		/// </summary>
		private static bool IsReadyForCommands()
		{
			return _conn != null && _deviceID != null && _deviceConnected;
		}
		
		/// <summary>
		/// Execute profile switch command
		/// </summary>
		private static void ExecuteProfileSwitch(string profileName)
		{
			try
			{
				Debug.WriteLine($"⚡ SDeck.ExecuteProfileSwitch: Switching to {profileName}");
				
				if (_conn != null && _deviceID != null)
				{
					// Check if we're already in the target profile and keys are ready
					bool alreadyInProfile = _currentProfile == profileName;
					bool keysAlreadyReady = _virtualKeysReady && profileName == PROFILE_NAME;
					
					// If we're already in NWRS AC profile with keys ready, no need to switch
					if (alreadyInProfile && keysAlreadyReady)
					{
						Debug.WriteLine($"✅ SDeck.ExecuteProfileSwitch: Already in {profileName} with keys ready, skipping switch");
						return;
					}
					
					_conn.SwitchToProfileAsync(_deviceID, profileName, _pluginUUID);
					
					// Only update state if we're actually changing profiles
					bool profileChanged = _currentProfile != profileName;
					_currentProfile = profileName;
					
					// Track when we switch to NWRS for recovery logic
					if (profileName == PROFILE_NAME)
					{
						// Only reset keys and timestamp if we actually changed profiles
						if (profileChanged)
						{
							_lastProfileSwitchToNWRS = DateTime.Now;
							_virtualKeysReady = false;
							Debug.WriteLine($"✅ SDeck.ExecuteProfileSwitch: Switched to {profileName} (profile changed, waiting for keys)");
						}
						else
						{
							Debug.WriteLine($"✅ SDeck.ExecuteProfileSwitch: Already in {profileName}, keeping key status: {_virtualKeysReady}");
						}
					}
					else
					{
						_virtualKeysReady = false;
						Debug.WriteLine($"✅ SDeck.ExecuteProfileSwitch: Switched to {profileName}");
					}
				}
				else
				{
					Debug.WriteLine($"❌ SDeck.ExecuteProfileSwitch: Connection not ready for {profileName}");
					_pendingCommands.Enqueue(() => ExecuteProfileSwitch(profileName));
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ SDeck.ExecuteProfileSwitch: Error switching to {profileName}: {ex.Message}");
				_pendingCommands.Enqueue(() => ExecuteProfileSwitch(profileName));
			}
		}
		
		/// <summary>
		/// Execute VPage set command
		/// </summary>
		private static void ExecuteVPageSet(VPage vPage)
		{
			try
			{
				Debug.WriteLine($"⚡ SDeck.ExecuteVPageSet: Setting VPage {vPage?.Name ?? "null"}");
				Debug.WriteLine($"🔍 Checking conditions: Profile={_currentProfile} (need {PROFILE_NAME}), VKeys={_virtualKeysReady} (need true)");
				Debug.WriteLine($"🔍 Additional status: Connected={_deviceConnected}, DeviceID={_deviceID != null}, Conn={_conn != null}");
				
				// Only set VPage if we're in NWRS profile
				if (_currentProfile != PROFILE_NAME)
				{
					Debug.WriteLine($"⚠️ SDeck.ExecuteVPageSet: Not in {PROFILE_NAME} profile ({_currentProfile}), re-queuing");
					_pendingCommands.Enqueue(() => ExecuteVPageSet(vPage));
					return;
				}
				
				// Only set VPage if virtual keys are ready
				if (!_virtualKeysReady)
				{
					Debug.WriteLine($"⚠️ SDeck.ExecuteVPageSet: Virtual keys not ready, re-queuing");
					_pendingCommands.Enqueue(() => ExecuteVPageSet(vPage));
					return;
				}
				
				Debug.WriteLine($"✅ SDeck.ExecuteVPageSet: Conditions met, setting VPage on SDPage");
				SDPage.SetVPage(vPage);
				_currentVPage = vPage;
				Debug.WriteLine($"✅ SDeck.ExecuteVPageSet: Set VPage {vPage?.Name ?? "null"}");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ SDeck.ExecuteVPageSet: Error setting VPage: {ex.Message}");
				_pendingCommands.Enqueue(() => ExecuteVPageSet(vPage));
			}
		}
		
		/// <summary>
		/// Establish the actual StreamDeck connection
		/// </summary>
		private static void EstablishStreamDeckConnection(int port, string pluginUUID, string registerEvent)
		{
			try
			{
				Debug.WriteLine($"🔗 SDeck: Establishing StreamDeck connection on port {port}");
				
				_pluginUUID = pluginUUID;
				_conn = new StreamDeckConnection(port, pluginUUID, registerEvent);
				Conn = _conn;
				
				// Set up event handlers
				_conn.OnConnected += OnStreamDeckConnected;
				_conn.OnDisconnected += OnStreamDeckDisconnected;
				_conn.OnDeviceDidConnect += OnDeviceConnected;
				_conn.OnDeviceDidDisconnect += OnDeviceDisconnected;
				_conn.OnWillAppear += OnKeyAppeared;
				_conn.OnWillDisappear += OnKeyDisappeared;
				_conn.OnKeyDown += OnKeyDown;
				_conn.OnKeyUp += OnKeyUp;
				
				// Start the connection
				_conn.Run();
				
				Debug.WriteLine("✅ SDeck: StreamDeck connection established");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ SDeck.EstablishStreamDeckConnection: {ex.Message}");
				_conn = null;
				Conn = null;
				throw;
			}
		}
		
		/// <summary>
		/// Event handlers for StreamDeck connection
		/// </summary>
		private static void OnStreamDeckConnected(object sender, EventArgs e)
		{
			Debug.WriteLine("🔗 SDeck: StreamDeck connected");
		}
		
		private static void OnStreamDeckDisconnected(object sender, EventArgs e)
		{
			Debug.WriteLine("❌ SDeck: StreamDeck disconnected - plugin will be restarted by StreamDeck software");
			lock (_stateLock)
			{
				_deviceConnected = false;
				_virtualKeysReady = false;
			}
			_conn = null;
			Conn = null;
			
			// StreamDeck software will restart us, so just exit
			Environment.Exit(0);
		}
		
		private static void OnDeviceConnected(object sender, StreamDeckEventReceivedEventArgs<DeviceDidConnectEvent> e)
		{
			Debug.WriteLine($"🔗 SDeck: Device connected: {e.Event.Device}");
			lock (_stateLock)
			{
				_deviceID = e.Event.Device;
				_deviceConnected = true;
				
				Debug.WriteLine($"🔗 SDeck: Device connection established - Current profile: {_currentProfile}, Desired: {_desiredProfile}");
				
				// Always ensure we're in the desired profile when device connects
				if (_desiredProfile != _currentProfile)
				{
					Debug.WriteLine($"🔧 SDeck: Device connected but profile mismatch - switching from {_currentProfile} to {_desiredProfile}");
					_pendingCommands.Enqueue(() => ExecuteProfileSwitch(_desiredProfile));
				}
				else
				{
					Debug.WriteLine($"🔧 SDeck: Device connected and profile matches - ensuring {PROFILE_NAME} virtual keys appear");
					if (_desiredProfile == PROFILE_NAME)
					{
						_pendingCommands.Enqueue(() => ExecuteProfileSwitch(PROFILE_NAME));
					}
				}
			}
		}
		
		private static void OnDeviceDisconnected(object sender, StreamDeckEventReceivedEventArgs<DeviceDidDisconnectEvent> e)
		{
			Debug.WriteLine($"❌ SDeck: Device disconnected: {e.Event.Device}");
			lock (_stateLock)
			{
				_deviceConnected = false;
				_virtualKeysReady = false;
				_deviceID = null;
			}
		}
		
		private static void OnKeyAppeared(object sender, StreamDeckEventReceivedEventArgs<WillAppearEvent> e)
		{
			if (e.Event.Action == "com.nwracingsims.acuic.virtualkey")
			{
				Debug.WriteLine($"🔑 SDeck: Virtual key appeared at {e.Event.Payload.Coordinates.Columns},{e.Event.Payload.Coordinates.Rows}");
				Debug.WriteLine($"🔍 Virtual key context: {e.Event.Context}");
				
				SDPage.AddKey(e.Event.Payload.Coordinates, e.Event.Context);
				
				lock (_stateLock)
				{
					_virtualKeysReady = true;
					
					// If we don't know what profile we're in yet, but keys are appearing, 
					// we must be in the NWRS AC profile already
					if (_currentProfile == null)
					{
						_currentProfile = PROFILE_NAME;
						_lastProfileSwitchToNWRS = DateTime.Now;
						Debug.WriteLine($"✅ SDeck: Virtual keys appeared - we're in '{PROFILE_NAME}' profile");
					}
					else
					{
						Debug.WriteLine($"✅ SDeck: Virtual keys now ready! Current profile: {_currentProfile}");
					}
					
					// Set desired VPage if we have one pending
					if (_desiredVPage != null && _currentProfile == PROFILE_NAME)
					{
						Debug.WriteLine($"🔄 SDeck: Queuing VPage set for {_desiredVPage.Name} since keys are now ready");
						_pendingCommands.Enqueue(() => ExecuteVPageSet(_desiredVPage));
					}
					else if (_desiredVPage != null)
					{
						Debug.WriteLine($"⚠️ SDeck: Have desired VPage ({_desiredVPage.Name}) but not in {PROFILE_NAME} profile ({_currentProfile})");
					}
					else
					{
						Debug.WriteLine($"⚠️ SDeck: Virtual keys ready but no desired VPage set");
					}
				}
			}
			else if (e.Event.Action == "com.nwracingsims.acuic.backkey")
			{
				Debug.WriteLine($"🔙 SDeck: Back key appeared: {e.Event.Context}");
				_backKey = new BackKey(e.Event.Context);
			}
		}
		
		private static void OnKeyDisappeared(object sender, StreamDeckEventReceivedEventArgs<WillDisappearEvent> e)
		{
			if (e.Event.Action == "com.nwracingsims.acuic.virtualkey")
			{
				Debug.WriteLine($"🔑 SDeck: Virtual key disappeared from {e.Event.Payload.Coordinates.Columns},{e.Event.Payload.Coordinates.Rows}");
				SDPage.RemoveKey(e.Event.Payload.Coordinates, e.Event.Context);
				
				// Check if all virtual keys are gone
				var remainingKeys = SDPage.SDKeys.Cast<SDKey>().Count(k => k.Context != null);
				if (remainingKeys == 0)
				{
					lock (_stateLock)
					{
						_virtualKeysReady = false;
					}
				}
			}
			else if (e.Event.Action == "com.nwracingsims.acuic.backkey")
			{
				Debug.WriteLine($"🔙 SDeck: Back key disappeared: {e.Event.Context}");
				_backKey = null;
			}
		}
		
		private static void OnKeyDown(object sender, StreamDeckEventReceivedEventArgs<KeyDownEvent> e)
		{
			// Only process keys when in NWRS AC profile
			if (_currentProfile == PROFILE_NAME)
			{
				if (_backKey != null && e.Event.Context == _backKey.GetContext())
				{
					// Handle back key
				}
				else
				{
					SDPage.OnKeyDown(e.Event.Payload.Coordinates, e.Event.Context);
				}
			}
		}
		
		private static void OnKeyUp(object sender, StreamDeckEventReceivedEventArgs<KeyUpEvent> e)
		{
			// Only process keys when in NWRS AC profile
			if (_currentProfile == PROFILE_NAME)
			{
				if (_backKey != null && e.Event.Context == _backKey.GetContext())
				{
					// Handle back key
				}
				else
				{
					SDPage.OnKeyUp(e.Event.Payload.Coordinates, e.Event.Context);
				}
			}
		}
	}
}
