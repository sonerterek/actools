using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AcManager.UiObserver
{
    #region Data Models

    /// <summary>
    /// Represents a StreamDeck key definition with title and icon.
    /// </summary>
    public class SDPKeyDef
    {
        /// <summary>
        /// Unique identifier for the key (no spaces).
        /// </summary>
        public string KeyName { get; set; }

        /// <summary>
        /// Display title shown on the key (optional - use null/"" for blank/icon-only keys).
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Icon specification (optional - use null/"" for blank/title-only keys):
        /// - Full file path: "C:\...\icon.png"
        /// - Text-based: "!AB" (generates icon with text)
        /// - Base64: "data:image/png;base64,..." (embedded image)
        /// - Blank: null or "" (no icon)
        /// </summary>
        public string IconSpec { get; set; }

        public SDPKeyDef(string keyName, string title = null, string iconSpec = null)
        {
            KeyName = keyName;
            Title = title;
            IconSpec = iconSpec;
        }

        /// <summary>
        /// Formats this key definition as a protocol command.
        /// Format: DefineKey &lt;KeyName&gt; [Title] [IconFileName]
        /// Protocol change: Title now comes BEFORE IconFileName
        /// </summary>
        public string ToCommand()
        {
            var parts = new List<string> { "DefineKey", KeyName };

            // Add title if not null/empty
            if (!string.IsNullOrEmpty(Title))
            {
                parts.Add($"\"{Title}\"");
            }
            else if (IconSpec != null)
            {
                // If icon is specified but title is empty, use "null" placeholder
                parts.Add("null");
            }

            // Add icon if not null/empty
            if (!string.IsNullOrEmpty(IconSpec))
            {
                parts.Add(IconSpec);
            }

            return string.Join(" ", parts);
        }
    }

    /// <summary>
    /// Represents a StreamDeck page with a 5x3 grid layout.
    /// </summary>
    public class SDPPageDef
    {
        /// <summary>
        /// Unique identifier for the page (no spaces).
        /// </summary>
        public string PageName { get; set; }

        /// <summary>
        /// 5x3 grid of key names. Use null or empty string for blank positions.
        /// Grid[row][column] where row is 0-4 and column is 0-2.
        /// </summary>
        public string[][] KeyGrid { get; set; }

        public SDPPageDef(string pageName)
        {
            PageName = pageName;
            // Initialize empty 5x3 grid
            KeyGrid = new string[5][];
            for (int i = 0; i < 5; i++)
            {
                KeyGrid[i] = new string[3];
            }
        }

        /// <summary>
        /// Sets a key at the specified position.
        /// </summary>
        /// <param name="row">Row index (0-4)</param>
        /// <param name="col">Column index (0-2)</param>
        /// <param name="keyName">Key name or null for empty</param>
        public void SetKey(int row, int col, string keyName)
        {
            if (row < 0 || row >= 5 || col < 0 || col >= 3)
            {
                throw new ArgumentOutOfRangeException($"Invalid position: [{row},{col}]. Must be [0-4, 0-2]");
            }
            KeyGrid[row][col] = keyName;
        }

        /// <summary>
        /// Gets a key at the specified position.
        /// </summary>
        public string GetKey(int row, int col)
        {
            if (row < 0 || row >= 5 || col < 0 || col >= 3)
            {
                return null;
            }
            return KeyGrid[row][col];
        }

        /// <summary>
        /// Formats this page definition as a protocol command.
        /// Format: DefinePage &lt;PageName&gt; &lt;KeyGrid&gt;
        /// </summary>
        public string ToCommand()
        {
            // Convert grid to JSON array format
            var jsonGrid = JsonConvert.SerializeObject(KeyGrid);
            return $"DefinePage {PageName} {jsonGrid}";
        }
    }

    /// <summary>
    /// Event arguments for StreamDeck key press events.
    /// </summary>
    public class SDPKeyPressEventArgs : EventArgs
    {
        public string KeyName { get; set; }
        public DateTime Timestamp { get; set; }

        public SDPKeyPressEventArgs(string keyName)
        {
            KeyName = keyName;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// Event arguments for KeyDefined response events.
    /// </summary>
    public class SDPKeyDefinedEventArgs : EventArgs
    {
        public string KeyName { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }

        public SDPKeyDefinedEventArgs(string keyName, bool success, string errorMessage = null)
        {
            KeyName = keyName;
            Success = success;
            ErrorMessage = errorMessage;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// Event arguments for KeyVisualsSet response events.
    /// </summary>
    public class SDPKeyVisualsSetEventArgs : EventArgs
    {
        public string KeyName { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }

        public SDPKeyVisualsSetEventArgs(string keyName, bool success, string errorMessage = null)
        {
            KeyName = keyName;
            Success = success;
            ErrorMessage = errorMessage;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// Event arguments for PageDefined response events.
    /// </summary>
    public class SDPPageDefinedEventArgs : EventArgs
    {
        public string PageName { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }

        public SDPPageDefinedEventArgs(string pageName, bool success, string errorMessage = null)
        {
            PageName = pageName;
            Success = success;
            ErrorMessage = errorMessage;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// Result of batch definition operations.
    /// </summary>
    public class SDPBatchResult
    {
        public int TotalCount { get; set; }
        public int SuccessCount { get; set; }
        public List<SDPKeyDefinedEventArgs> FailedKeys { get; set; }
        public List<SDPPageDefinedEventArgs> FailedPages { get; set; }

        public bool AllSucceeded => SuccessCount == TotalCount;
        
        public string ErrorSummary
        {
            get
            {
                var errors = new List<string>();
                
                if (FailedKeys != null)
                {
                    errors.AddRange(FailedKeys.Select(f => $"Key '{f.KeyName}': {f.ErrorMessage}"));
                }
                
                if (FailedPages != null)
                {
                    errors.AddRange(FailedPages.Select(f => $"Page '{f.PageName}': {f.ErrorMessage}"));
                }
                
                return errors.Count > 0 ? string.Join("\n", errors) : null;
            }
        }

        public SDPBatchResult()
        {
            FailedKeys = new List<SDPKeyDefinedEventArgs>();
            FailedPages = new List<SDPPageDefinedEventArgs>();
        }
    }

    #endregion

    #region Client Implementation

    /// <summary>
    /// Client for communicating with the NWRS AC StreamDeck Plugin via Named Pipe.
    /// 
    /// Protocol:
    /// - Named Pipe: "NWRS_AC_SDPlugin_Pipe"
    /// - Commands (CM ? Plugin): DefineKey, SetKeyVisuals, DefinePage, SwitchPage
    /// - Events (Plugin ? CM): KeyPress, KeyDefined, KeyVisualsSet, PageDefined
    /// 
    /// All definition operations are async and wait for plugin confirmation.
    /// 
    /// Lifecycle:
    /// 1. ConnectAsync() - Establishes pipe connection
    /// 2. DefineKeyAsync() - Register keys and wait for confirmation
    /// 3. DefinePageAsync() - Create page layouts and wait for confirmation
    /// 4. SwitchPage() - Change active page (fire-and-forget)
    /// 5. SetKeyVisualsAsync() - Update keys in current page (page-specific)
    /// 6. Listen for KeyPress events
    /// 7. Dispose() - Clean disconnect
    /// </summary>
    public class SDPClient : IDisposable
    {
        #region Constants

        private const string PipeName = "NWRS_AC_SDPlugin_Pipe";
        private const int ConnectionTimeoutMs = 2000;
        private const int ResponseTimeoutMs = 5000;

        #endregion

        #region Fields

        private NamedPipeClientStream _pipe;
        private StreamWriter _writer;
        private StreamReader _reader;
        private CancellationTokenSource _listenerCts;
        private Task _listenerTask;
        private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        private readonly object _writeLock = new object();

        // Pending response tracking
        private readonly Dictionary<string, TaskCompletionSource<SDPKeyDefinedEventArgs>> _pendingKeyDefs
            = new Dictionary<string, TaskCompletionSource<SDPKeyDefinedEventArgs>>();
        private readonly Dictionary<string, TaskCompletionSource<SDPKeyVisualsSetEventArgs>> _pendingKeyVisuals
            = new Dictionary<string, TaskCompletionSource<SDPKeyVisualsSetEventArgs>>();
        private readonly Dictionary<string, TaskCompletionSource<SDPPageDefinedEventArgs>> _pendingPageDefs
            = new Dictionary<string, TaskCompletionSource<SDPPageDefinedEventArgs>>();

        // Cache for successfully defined keys and pages
        private readonly HashSet<string> _definedKeys = new HashSet<string>();
        private readonly HashSet<string> _definedPages = new HashSet<string>();

        #endregion

        #region Events

        /// <summary>
        /// Raised when user presses a key on the StreamDeck.
        /// </summary>
        public event EventHandler<SDPKeyPressEventArgs> KeyPressed;

        /// <summary>
        /// Raised when connection to plugin is established.
        /// </summary>
        public event EventHandler Connected;

        /// <summary>
        /// Raised when connection to plugin is lost.
        /// </summary>
        public event EventHandler Disconnected;

        #endregion

        #region Properties

        /// <summary>
        /// Whether the client is currently connected to the plugin.
        /// </summary>
        public bool IsConnected => _pipe?.IsConnected == true;

        /// <summary>
        /// Number of keys successfully defined.
        /// </summary>
        public int KeyCount => _definedKeys.Count;

        /// <summary>
        /// Number of pages successfully defined.
        /// </summary>
        public int PageCount => _definedPages.Count;

        /// <summary>
        /// Enable verbose debug output.
        /// </summary>
        public bool VerboseDebug { get; set; } = false;

        #endregion

        #region Connection Management

        /// <summary>
        /// Connects to the StreamDeck plugin's named pipe server.
        /// Returns true if connection successful, false otherwise.
        /// </summary>
        public async Task<bool> ConnectAsync()
        {
            await _connectionLock.WaitAsync();
            try
            {
                if (IsConnected)
                {
                    Debug.WriteLine("[SDPClient] Already connected");
                    return true;
                }

                // Clean up old connection
                DisconnectInternal();

                Debug.WriteLine($"[SDPClient] Connecting to pipe: {PipeName}");

                // Create new pipe client
                _pipe = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: PipeName,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.Asynchronous);

                Debug.WriteLine("[SDPClient] Pipe client created, attempting connection...");

                // Connect with timeout (synchronous Connect doesn't support cancellation in .NET 4.5.2)
                // Use Task.Run to avoid blocking
                bool connected = await Task.Run(() =>
                {
                    try
                    {
                        Debug.WriteLine($"[SDPClient] Calling Connect() with timeout={ConnectionTimeoutMs}ms...");
                        _pipe.Connect(ConnectionTimeoutMs);
                        Debug.WriteLine($"[SDPClient] Connect() returned, IsConnected={_pipe.IsConnected}");
                        return _pipe.IsConnected;
                    }
                    catch (TimeoutException ex)
                    {
                        Debug.WriteLine($"[SDPClient] Connect() timed out: {ex.Message}");
                        return false;
                    }
                    catch (IOException ex)
                    {
                        Debug.WriteLine($"[SDPClient] Connect() IOException: {ex.Message}");
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SDPClient] Connect() unexpected exception: {ex.GetType().Name}: {ex.Message}");
                        return false;
                    }
                });

                if (!connected)
                {
                    Debug.WriteLine("[SDPClient] Connection failed - is plugin running?");
                    DisconnectInternal();
                    return false;
                }

                Debug.WriteLine("[SDPClient] Pipe connected, creating streams...");

                // Setup streams with explicit buffer sizes to match plugin
                try
                {
                    Debug.WriteLine("[SDPClient] Creating StreamWriter with UTF8 encoding...");
                    _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 1024) { AutoFlush = true };
                    Debug.WriteLine("[SDPClient] StreamWriter created successfully");
                    
                    Debug.WriteLine("[SDPClient] Creating StreamReader with UTF8 encoding...");
                    _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 1024);
                    Debug.WriteLine("[SDPClient] StreamReader created successfully");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SDPClient] Failed to create streams: {ex.GetType().Name}: {ex.Message}");
                    Debug.WriteLine($"[SDPClient] Stack trace: {ex.StackTrace}");
                    throw;
                }

                Debug.WriteLine("[SDPClient] Streams created successfully, starting listener...");

                // Start listener for incoming events
                _listenerCts = new CancellationTokenSource();
                Debug.WriteLine("[SDPClient] Created CancellationTokenSource");
                
                _listenerTask = Task.Run(() => ListenForEventsAsync(_listenerCts.Token));
                Debug.WriteLine("[SDPClient] Listener task started");
                
                // Give the listener a moment to start (use Task.Delay for .NET 4.5.2 compatibility)
                Debug.WriteLine("[SDPClient] Waiting 100ms for listener to start...");
                await Task.Delay(100).ConfigureAwait(false);
                
                Debug.WriteLine($"[SDPClient] After delay - Pipe still connected: {_pipe.IsConnected}");

                Debug.WriteLine("[SDPClient] Connected successfully");
                OnConnected();

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SDPClient] Connection error: {ex.GetType().Name}: {ex.Message}");
                Debug.WriteLine($"[SDPClient] Stack trace: {ex.StackTrace}");
                DisconnectInternal();
                return false;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// Disconnects from the plugin and cleans up resources.
        /// </summary>
        public void Disconnect()
        {
            _connectionLock.Wait();
            try
            {
                DisconnectInternal();
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private void DisconnectInternal()
        {
            if (VerboseDebug) Debug.WriteLine("[SDPClient] Disconnecting...");

            // Stop listener
            _listenerCts?.Cancel();
            _listenerTask?.Wait(500);
            _listenerCts?.Dispose();
            _listenerCts = null;
            _listenerTask = null;

            // Fail all pending operations
            lock (_pendingKeyDefs)
            {
                foreach (var pending in _pendingKeyDefs.Values)
                {
                    pending.TrySetException(new IOException("Connection closed"));
                }
                _pendingKeyDefs.Clear();
            }

            lock (_pendingKeyVisuals)
            {
                foreach (var pending in _pendingKeyVisuals.Values)
                {
                    pending.TrySetException(new IOException("Connection closed"));
                }
                _pendingKeyVisuals.Clear();
            }

            lock (_pendingPageDefs)
            {
                foreach (var pending in _pendingPageDefs.Values)
                {
                    pending.TrySetException(new IOException("Connection closed"));
                }
                _pendingPageDefs.Clear();
            }

            // Close streams
            _writer?.Dispose();
            _writer = null;
            _reader?.Dispose();
            _reader = null;
            _pipe?.Dispose();
            _pipe = null;

            // Clear caches (plugin will clear on disconnect)
            _definedKeys.Clear();
            _definedPages.Clear();

            Debug.WriteLine("[SDPClient] Disconnected");
            OnDisconnected();
        }

        #endregion

        #region Command Sending

        /// <summary>
        /// Sends a command to the plugin.
        /// Returns true if sent successfully, false otherwise.
        /// </summary>
        private bool SendCommand(string command)
        {
            if (!IsConnected)
            {
                if (VerboseDebug) Debug.WriteLine($"[SDPClient] Cannot send command - not connected: {command}");
                return false;
            }

            lock (_writeLock)
            {
                try
                {
                    _writer.WriteLine(command);

                    if (VerboseDebug) Debug.WriteLine($"[SDPClient] Sent: {command}");
                    return true;
                }
                catch (IOException ex)
                {
                    if (VerboseDebug) Debug.WriteLine($"[SDPClient] Send failed: {ex.Message}");
                    // Connection lost - disconnect
                    Task.Run(() => Disconnect());
                    return false;
                }
            }
        }

        /// <summary>
        /// Defines a key and waits for confirmation from the plugin.
        /// Returns result with Success/ErrorMessage.
        /// 
        /// Protocol: DefineKey &lt;KeyName&gt; [Title] [IconFileName]
        /// Title comes BEFORE IconFileName (changed in protocol v1.1)
        /// </summary>
        public async Task<SDPKeyDefinedEventArgs> DefineKeyAsync(SDPKeyDef keyDef)
        {
            if (keyDef == null) throw new ArgumentNullException(nameof(keyDef));
            if (string.IsNullOrWhiteSpace(keyDef.KeyName))
            {
                throw new ArgumentException("KeyName cannot be empty", nameof(keyDef));
            }

            // Create TaskCompletionSource for this key
            var tcs = new TaskCompletionSource<SDPKeyDefinedEventArgs>();
            
            lock (_pendingKeyDefs)
            {
                _pendingKeyDefs[keyDef.KeyName] = tcs;
            }

            try
            {
                // Send command
                if (!SendCommand(keyDef.ToCommand()))
                {
                    lock (_pendingKeyDefs)
                    {
                        _pendingKeyDefs.Remove(keyDef.KeyName);
                    }
                    return new SDPKeyDefinedEventArgs(keyDef.KeyName, false, "Failed to send command");
                }

                // Wait for KeyDefined event with timeout
                var timeoutTask = Task.Delay(ResponseTimeoutMs);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    lock (_pendingKeyDefs)
                    {
                        _pendingKeyDefs.Remove(keyDef.KeyName);
                    }
                    return new SDPKeyDefinedEventArgs(keyDef.KeyName, false, "Timeout waiting for plugin response");
                }

                var result = await tcs.Task;
                
                // Update cache on success
                if (result.Success)
                {
                    _definedKeys.Add(keyDef.KeyName);
                }

                return result;
            }
            catch (Exception ex)
            {
                lock (_pendingKeyDefs)
                {
                    _pendingKeyDefs.Remove(keyDef.KeyName);
                }
                return new SDPKeyDefinedEventArgs(keyDef.KeyName, false, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Defines a key using individual parameters.
        /// Convenience method for DefineKeyAsync(SDPKeyDef).
        /// 
        /// Parameters:
        /// - keyName: Unique identifier (required)
        /// - title: Display title (optional - null for blank/icon-only)
        /// - iconSpec: Icon path or format (optional - null for blank/title-only)
        /// </summary>
        public Task<SDPKeyDefinedEventArgs> DefineKeyAsync(string keyName, string title = null, string iconSpec = null)
        {
            return DefineKeyAsync(new SDPKeyDef(keyName, title, iconSpec));
        }

        /// <summary>
        /// Defines multiple keys and returns aggregated results.
        /// Continues even if some keys fail to define.
        /// </summary>
        public async Task<SDPBatchResult> DefineKeysAsync(IEnumerable<SDPKeyDef> keys)
        {
            var result = new SDPBatchResult();
            var keysList = keys.ToList();
            result.TotalCount = keysList.Count;

            foreach (var key in keysList)
            {
                var keyResult = await DefineKeyAsync(key);
                
                if (keyResult.Success)
                {
                    result.SuccessCount++;
                }
                else
                {
                    result.FailedKeys.Add(keyResult);
                }
            }

            return result;
        }

        /// <summary>
        /// Updates the title and/or icon of a key in the currently active page only.
        /// Does not affect the key definition or other pages.
        /// 
        /// Protocol: SetKeyVisuals &lt;KeyName&gt; [Title] [IconFileName]
        /// 
        /// Use cases:
        /// - Real-time status updates
        /// - Dynamic labels
        /// - Page-specific key appearances
        /// </summary>
        public async Task<SDPKeyVisualsSetEventArgs> SetKeyVisualsAsync(string keyName, string title = null, string iconSpec = null)
        {
            if (string.IsNullOrWhiteSpace(keyName))
            {
                throw new ArgumentException("KeyName cannot be empty", nameof(keyName));
            }

            // Create TaskCompletionSource for this operation
            var tcs = new TaskCompletionSource<SDPKeyVisualsSetEventArgs>();
            
            lock (_pendingKeyVisuals)
            {
                _pendingKeyVisuals[keyName] = tcs;
            }

            try
            {
                // Build command
                var parts = new List<string> { "SetKeyVisuals", keyName };

                // Add title if not null
                if (title != null)
                {
                    parts.Add(string.IsNullOrEmpty(title) ? "null" : $"\"{title}\"");
                }
                else if (iconSpec != null)
                {
                    // If icon is specified but title is omitted, use "null" placeholder
                    parts.Add("null");
                }

                // Add icon if not null
                if (iconSpec != null)
                {
                    parts.Add(string.IsNullOrEmpty(iconSpec) ? "null" : iconSpec);
                }

                var command = string.Join(" ", parts);

                // Send command
                if (!SendCommand(command))
                {
                    lock (_pendingKeyVisuals)
                    {
                        _pendingKeyVisuals.Remove(keyName);
                    }
                    return new SDPKeyVisualsSetEventArgs(keyName, false, "Failed to send command");
                }

                // Wait for KeyVisualsSet event with timeout
                var timeoutTask = Task.Delay(ResponseTimeoutMs);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    lock (_pendingKeyVisuals)
                    {
                        _pendingKeyVisuals.Remove(keyName);
                    }
                    return new SDPKeyVisualsSetEventArgs(keyName, false, "Timeout waiting for plugin response");
                }

                return await tcs.Task;
            }
            catch (Exception ex)
            {
                lock (_pendingKeyVisuals)
                {
                    _pendingKeyVisuals.Remove(keyName);
                }
                return new SDPKeyVisualsSetEventArgs(keyName, false, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Defines a page and waits for confirmation from the plugin.
        /// Returns result with Success/ErrorMessage.
        /// 
        /// Note: Plugin validates that all referenced keys exist.
        /// Page creation fails completely if ANY key is undefined.
        /// </summary>
        public async Task<SDPPageDefinedEventArgs> DefinePageAsync(SDPPageDef pageDef)
        {
            if (pageDef == null) throw new ArgumentNullException(nameof(pageDef));
            if (string.IsNullOrWhiteSpace(pageDef.PageName))
            {
                throw new ArgumentException("PageName cannot be empty", nameof(pageDef));
            }

            // Validate grid dimensions
            if (pageDef.KeyGrid == null || pageDef.KeyGrid.Length != 5)
            {
                return new SDPPageDefinedEventArgs(pageDef.PageName, false, "KeyGrid must have exactly 5 rows");
            }
            
            for (int i = 0; i < 5; i++)
            {
                if (pageDef.KeyGrid[i] == null || pageDef.KeyGrid[i].Length != 3)
                {
                    return new SDPPageDefinedEventArgs(pageDef.PageName, false, $"KeyGrid row {i} must have exactly 3 columns");
                }
            }

            // Create TaskCompletionSource for this page
            var tcs = new TaskCompletionSource<SDPPageDefinedEventArgs>();
            
            lock (_pendingPageDefs)
            {
                _pendingPageDefs[pageDef.PageName] = tcs;
            }

            try
            {
                // Send command
                if (!SendCommand(pageDef.ToCommand()))
                {
                    lock (_pendingPageDefs)
                    {
                        _pendingPageDefs.Remove(pageDef.PageName);
                    }
                    return new SDPPageDefinedEventArgs(pageDef.PageName, false, "Failed to send command");
                }

                // Wait for PageDefined event with timeout
                var timeoutTask = Task.Delay(ResponseTimeoutMs);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    lock (_pendingPageDefs)
                    {
                        _pendingPageDefs.Remove(pageDef.PageName);
                    }
                    return new SDPPageDefinedEventArgs(pageDef.PageName, false, "Timeout waiting for plugin response");
                }

                var result = await tcs.Task;
                
                // Update cache on success
                if (result.Success)
                {
                    _definedPages.Add(pageDef.PageName);
                }

                return result;
            }
            catch (Exception ex)
            {
                lock (_pendingPageDefs)
                {
                    _pendingPageDefs.Remove(pageDef.PageName);
                }
                return new SDPPageDefinedEventArgs(pageDef.PageName, false, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Defines multiple pages and returns aggregated results.
        /// Continues even if some pages fail to define.
        /// </summary>
        public async Task<SDPBatchResult> DefinePagesAsync(IEnumerable<SDPPageDef> pages)
        {
            var result = new SDPBatchResult();
            var pagesList = pages.ToList();
            result.TotalCount = pagesList.Count;

            foreach (var page in pagesList)
            {
                var pageResult = await DefinePageAsync(page);
                
                if (pageResult.Success)
                {
                    result.SuccessCount++;
                }
                else
                {
                    result.FailedPages.Add(pageResult);
                }
            }

            return result;
        }

        /// <summary>
        /// Switches to the specified page (fire-and-forget).
        /// Page must be defined first with DefinePageAsync.
        /// </summary>
        public bool SwitchPage(string pageName)
        {
            if (string.IsNullOrWhiteSpace(pageName))
            {
                throw new ArgumentException("PageName cannot be empty", nameof(pageName));
            }

            return SendCommand($"SwitchPage {pageName}");
        }

        #endregion

        #region Event Listening

        /// <summary>
        /// Background task that listens for events from the plugin.
        /// </summary>
        private async Task ListenForEventsAsync(CancellationToken cancellationToken)
        {
            Debug.WriteLine("[SDPClient] Event listener started");

            try
            {
                Debug.WriteLine("[SDPClient] Listener: Starting to read from pipe...");
                
                while (!cancellationToken.IsCancellationRequested && _pipe?.IsConnected == true)
                {
                    try
                    {
                        Debug.WriteLine("[SDPClient] Listener: Calling ReadLineAsync()...");
                        var line = await _reader.ReadLineAsync();
                        Debug.WriteLine($"[SDPClient] Listener: ReadLineAsync() returned: {(line == null ? "NULL" : $"\"{line}\"")}");

                        if (line == null)
                        {
                            // Connection closed
                            Debug.WriteLine("[SDPClient] Plugin disconnected (ReadLineAsync returned null)");
                            break;
                        }

                        // Process event
                        ProcessEvent(line);
                    }
                    catch (IOException ex)
                    {
                        // Connection lost
                        Debug.WriteLine($"[SDPClient] Connection lost while reading: {ex.Message}");
                        Debug.WriteLine($"[SDPClient] IOException stack trace: {ex.StackTrace}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SDPClient] Event processing error: {ex.GetType().Name}: {ex.Message}");
                        Debug.WriteLine($"[SDPClient] Stack trace: {ex.StackTrace}");
                        
                        // Don't break on processing errors, continue listening
                    }
                }
                
                Debug.WriteLine($"[SDPClient] Listener loop exited. Cancelled={cancellationToken.IsCancellationRequested}, PipeConnected={_pipe?.IsConnected}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SDPClient] Listener fatal error: {ex.GetType().Name}: {ex.Message}");
                Debug.WriteLine($"[SDPClient] Stack trace: {ex.StackTrace}");
            }
            finally
            {
                Debug.WriteLine("[SDPClient] Event listener stopped");
                // Trigger disconnect if not already disconnecting
                if (_pipe?.IsConnected == true)
                {
                    Debug.WriteLine("[SDPClient] Listener triggering disconnect...");
                    Task.Run(() => Disconnect());
                }
            }
        }

        /// <summary>
        /// Processes an event message from the plugin.
        /// Formats:
        ///   - "KeyPress &lt;KeyName&gt;"
        ///   - "KeyDefined &lt;KeyName&gt; &lt;Status&gt; [ErrorMessage]"
        ///   - "KeyVisualsSet &lt;KeyName&gt; &lt;Status&gt; [ErrorMessage]"
        ///   - "PageDefined &lt;PageName&gt; &lt;Status&gt; [ErrorMessage]"
        /// </summary>
        private void ProcessEvent(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            // Strip UTF-8 BOM if present (0xFEFF / U+FEFF)
            if (message.Length > 0 && message[0] == '\uFEFF')
            {
                message = message.Substring(1);
            }

            if (VerboseDebug) Debug.WriteLine($"[SDPClient] Received: {message}");

            // Split into max 3 parts: EventType, Name, StatusAndError
            var parts = message.Split(new[] { ' ' }, 3);
            if (parts.Length < 2) return;

            var eventType = parts[0];

            switch (eventType)
            {
                case "KeyPress":
                    OnKeyPressed(parts[1]);
                    break;

                case "KeyDefined":
                    ParseKeyDefinedEvent(parts);
                    break;

                case "KeyVisualsSet":
                    ParseKeyVisualsSetEvent(parts);
                    break;

                case "PageDefined":
                    ParsePageDefinedEvent(parts);
                    break;

                default:
                    if (VerboseDebug) Debug.WriteLine($"[SDPClient] Unknown event type: {eventType}");
                    break;
            }
        }

        private void ParseKeyDefinedEvent(string[] parts)
        {
            if (parts.Length < 3) return;

            var keyName = parts[1];
            var statusAndError = parts[2];

            // Parse status and error message
            var statusParts = statusAndError.Split(new[] { ' ' }, 2);
            var status = statusParts[0];
            var errorMessage = statusParts.Length > 1 ? statusParts[1] : null;

            var success = status == "OK";
            var eventArgs = new SDPKeyDefinedEventArgs(keyName, success, errorMessage);

            // Complete pending operation
            TaskCompletionSource<SDPKeyDefinedEventArgs> tcs = null;
            lock (_pendingKeyDefs)
            {
                if (_pendingKeyDefs.TryGetValue(keyName, out tcs))
                {
                    _pendingKeyDefs.Remove(keyName);
                }
            }

            if (tcs != null)
            {
                tcs.TrySetResult(eventArgs);
            }
            else if (VerboseDebug)
            {
                Debug.WriteLine($"[SDPClient] Received KeyDefined for '{keyName}' but no pending operation found");
            }
        }

        private void ParseKeyVisualsSetEvent(string[] parts)
        {
            if (parts.Length < 3) return;

            var keyName = parts[1];
            var statusAndError = parts[2];

            // Parse status and error message
            var statusParts = statusAndError.Split(new[] { ' ' }, 2);
            var status = statusParts[0];
            var errorMessage = statusParts.Length > 1 ? statusParts[1] : null;

            var success = status == "OK";
            var eventArgs = new SDPKeyVisualsSetEventArgs(keyName, success, errorMessage);

            // Complete pending operation
            TaskCompletionSource<SDPKeyVisualsSetEventArgs> tcs = null;
            lock (_pendingKeyVisuals)
            {
                if (_pendingKeyVisuals.TryGetValue(keyName, out tcs))
                {
                    _pendingKeyVisuals.Remove(keyName);
                }
            }

            if (tcs != null)
            {
                tcs.TrySetResult(eventArgs);
            }
            else if (VerboseDebug)
            {
                Debug.WriteLine($"[SDPClient] Received KeyVisualsSet for '{keyName}' but no pending operation found");
            }
        }

        private void ParsePageDefinedEvent(string[] parts)
        {
            if (parts.Length < 3) return;

            var pageName = parts[1];
            var statusAndError = parts[2];

            // Parse status and error message
            var statusParts = statusAndError.Split(new[] { ' ' }, 2);
            var status = statusParts[0];
            var errorMessage = statusParts.Length > 1 ? statusParts[1] : null;

            var success = status == "OK";
            var eventArgs = new SDPPageDefinedEventArgs(pageName, success, errorMessage);

            // Complete pending operation
            TaskCompletionSource<SDPPageDefinedEventArgs> tcs = null;
            lock (_pendingPageDefs)
            {
                if (_pendingPageDefs.TryGetValue(pageName, out tcs))
                {
                    _pendingPageDefs.Remove(pageName);
                }
            }

            if (tcs != null)
            {
                tcs.TrySetResult(eventArgs);
            }
            else if (VerboseDebug)
            {
                Debug.WriteLine($"[SDPClient] Received PageDefined for '{pageName}' but no pending operation found");
            }
        }

        #endregion

        #region Event Handlers

        protected virtual void OnConnected()
        {
            Connected?.Invoke(this, EventArgs.Empty);
        }

        protected virtual void OnDisconnected()
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        protected virtual void OnKeyPressed(string keyName)
        {
            KeyPressed?.Invoke(this, new SDPKeyPressEventArgs(keyName));
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Checks if a key has been successfully defined.
        /// </summary>
        public bool IsKeyDefined(string keyName)
        {
            return _definedKeys.Contains(keyName);
        }

        /// <summary>
        /// Checks if a page has been successfully defined.
        /// </summary>
        public bool IsPageDefined(string pageName)
        {
            return _definedPages.Contains(pageName);
        }

        /// <summary>
        /// Gets all successfully defined key names.
        /// </summary>
        public IEnumerable<string> GetDefinedKeyNames()
        {
            return _definedKeys.ToList();
        }

        /// <summary>
        /// Gets all successfully defined page names.
        /// </summary>
        public IEnumerable<string> GetDefinedPageNames()
        {
            return _definedPages.ToList();
        }

        #endregion

        #region IDisposable

        private bool _disposed = false;

        public void Dispose()
        {
            if (_disposed) return;

            Disconnect();
            _connectionLock?.Dispose();

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~SDPClient()
        {
            Dispose();
        }

        #endregion
    }

    #endregion

    #region Helper Classes

    /// <summary>
    /// Helper for discovering PNG icon files in the SDIcons directory.
    /// </summary>
    public static class SDPIconHelper
    {
        /// <summary>
        /// Gets the SDIcons directory path under CM's Assets folder.
        /// </summary>
        public static string GetIconsDirectory()
        {
            var assetsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
            return Path.Combine(assetsPath, "SDIcons");
        }

        /// <summary>
        /// Discovers all PNG icon files in the SDIcons directory.
        /// Returns a dictionary mapping filename (without extension) to full path.
        /// </summary>
        public static Dictionary<string, string> DiscoverIcons()
        {
            var iconsDir = GetIconsDirectory();
            var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(iconsDir))
            {
                Debug.WriteLine($"[SDPIconHelper] Icons directory not found: {iconsDir}");
                return icons;
            }

            try
            {
                var pngFiles = Directory.GetFiles(iconsDir, "*.png", SearchOption.TopDirectoryOnly);
                foreach (var file in pngFiles)
                {
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(file);
                    icons[nameWithoutExt] = file;
                }

                Debug.WriteLine($"[SDPIconHelper] Discovered {icons.Count} icons in {iconsDir}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SDPIconHelper] Error discovering icons: {ex.Message}");
            }

            return icons;
        }

        /// <summary>
        /// Gets the full path for an icon by name.
        /// Returns null if icon not found.
        /// </summary>
        public static string GetIconPath(string iconName)
        {
            if (string.IsNullOrWhiteSpace(iconName)) return null;

            var iconsDir = GetIconsDirectory();
            var iconPath = Path.Combine(iconsDir, iconName + ".png");

            return File.Exists(iconPath) ? iconPath : null;
        }

        /// <summary>
        /// Creates a text-based icon spec.
        /// Format: "!TextHere"
        /// </summary>
        public static string CreateTextIcon(string text)
        {
            return $"!{text}";
        }

        /// <summary>
        /// Validates that an icon file exists.
        /// </summary>
        public static bool IconExists(string iconNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(iconNameOrPath)) return false;

            // Text-based icons always valid
            if (iconNameOrPath.StartsWith("!")) return true;

            // Base64 icons always valid
            if (iconNameOrPath.StartsWith("data:image/")) return true;

            // Check if it's a full path
            if (File.Exists(iconNameOrPath)) return true;

            // Check if it's a name in SDIcons
            return GetIconPath(iconNameOrPath) != null;
        }
    }

    #endregion
}
