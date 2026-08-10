using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Polymarket_Markets_Tracker.DTOs;

namespace Polymarket_Markets_Tracker;

public sealed class ActiveMarketsManager
{
    private const ushort limit = 50; //max num of markets to actively track
    private readonly ConcurrentDictionary<string, ClusterWorker> _activeWorkers = new(); //track event id - event threads

    //was a HashSet<string>, this is thread-safe
    private readonly ConcurrentDictionary<string, byte> _activeAssetIds = new();

    //"Asset ID X belongs to Event ID Y"
    private readonly ConcurrentDictionary<string, string> _assetToEventMap = new();
    private readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };

    //this always points at whichever ClientWebSocket is currently live, so CheckToAddAsync/CheckToRemoveAsync can push live subscribe/unsubscribe updates to it without needing a full reconnect.
    private volatile ClientWebSocket? _globalWebSocket;

    // Serializes every SendAsync call across the initial subscribe, the PING heartbeat loop and on-demand subscribe/unsubscribe updates 
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private CancellationTokenSource _managerCts = new();
    private readonly HttpClient _httpClient;

    //singleton 
    private static readonly Lazy<ActiveMarketsManager> _instance = new (() => new ActiveMarketsManager());
    public static ActiveMarketsManager Instance => _instance.Value;

    private async Task GlobalWebSocketListenerAsync(CancellationToken token)
    {
        Uri wsUri = new Uri("wss://ws-subscriptions-clob.polymarket.com/ws/market");

        while (!token.IsCancellationRequested)
        {
            ClientWebSocket client = new ClientWebSocket();
            try
            {
                await client.ConnectAsync(wsUri, token);
                _globalWebSocket = client;
                Console.WriteLine("WebSocket connected.");
                Console.WriteLine("[WS] Connected! Sending subscription...");
                var sub = new
                {
                    assets_ids = _activeAssetIds.Keys.ToArray(),
                    type = "market"
                };

                string json = JsonSerializer.Serialize(sub);
                await SendRawAsync(client, json, token);
                Console.WriteLine($"[WS] Sent Orderbook Subscription for {_activeAssetIds.Count} assets.");

                //start heartbeat (PING) loop
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (client.State == WebSocketState.Open && !token.IsCancellationRequested)
                        {
                            await Task.Delay(10000, token);
                            await SendRawAsync(client, "PING", token);
                        }
                    }
                    catch { /* socket closing - outer loop handles reconnect */ }
                }, token);

                //receive loop - reassemble multi-frame messages before parsing.
                var buffer = new byte[1024 * 32];
                using var messageBuffer = new MemoryStream();

                while (client.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    messageBuffer.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        messageBuffer.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;

                    string rawJson = Encoding.UTF8.GetString(messageBuffer.ToArray());
                    
                    if (rawJson == "PONG") continue;

                    //Console.WriteLine($"[WS MESSAGE]: {rawJson.Substring(0, Math.Min(rawJson.Length, 150))}");

                    using (JsonDocument doc = JsonDocument.Parse(rawJson))
                    {
                        JsonElement root = doc.RootElement;

                        if (root.ValueKind == JsonValueKind.Array)
                        {
                            //if Polymarket sends a batch array, unroll it and handle each event
                            foreach (JsonElement element in root.EnumerateArray())
                            {
                                await RouteMessageElementAsync(element);
                            }
                        }
                        else if (root.ValueKind == JsonValueKind.Object)
                        {
                            //fallback safety case if they ever send a single flat object
                            await RouteMessageElementAsync(root);
                        }
                    }
                }

                Console.WriteLine("[WS] Loop ended (connection closed) - will attempt to reconnect.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WS Error: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_globalWebSocket, client))
                {
                    _globalWebSocket = null;
                }
                try { client.Dispose(); } catch { /* ignore */ }
            }

            if (!token.IsCancellationRequested)
            {
                await Task.Delay(5000, token);
            }
        }
    }

    //serializes all sends on the shared socket (initial subscribe + heartbeat + on-demand subscribe/unsubscribe updates all funnel through here).
    private async Task SendRawAsync(ClientWebSocket socket, string text, CancellationToken token)
    {
        await _sendLock.WaitAsync(token);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, token);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    //sends an incremental subscribe update on the ALREADY-OPEN connection - Polymarket's market channel supports this without needing to reconnect:
    //{ "assets_ids": [...], "operation": "subscribe" }
    //if the socket isn't currently connected the next (re)connect will pick up these assets anyway since it always subscribes with the full current _activeAssetIds set.
    public Task SubscribeToNewAssetsAsync(IEnumerable<string> newAssetIds)
    {
        var socket = _globalWebSocket;
        var ids = newAssetIds.ToArray();
        if (ids.Length == 0 || socket == null) return Task.CompletedTask;

        var payload = new { assets_ids = ids, operation = "subscribe" };
        return SendRawAsync(socket, JsonSerializer.Serialize(payload), _managerCts.Token);
    }

    public Task UnsubscribeFromAssetsAsync(IEnumerable<string> oldAssetIds)
    {
        var socket = _globalWebSocket;
        var ids = oldAssetIds.ToArray();
        if (ids.Length == 0 || socket == null) return Task.CompletedTask;

        var payload = new { assets_ids = ids, operation = "unsubscribe" };
        return SendRawAsync(socket, JsonSerializer.Serialize(payload), _managerCts.Token);
    }

    private ActiveMarketsManager()
    {
        _httpClient = new HttpClient();
        _httpClient.BaseAddress = new Uri("https://gamma-api.polymarket.com/");
        //default is 100s
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }
    
    public void StartManager()
    {
        //offload the infinite listening loop to a background thread instantly
        Console.WriteLine("Initializing global network routing...");
        Task.Run(() => GlobalWebSocketListenerAsync(_managerCts.Token));
    }

    public async Task CheckToAddAsync()
    {
        //get different events by params
        string endpoint = $"events?active=true&closed=false&order=volume24hr&ascending=false&limit={(int)(limit)}";        
        var newlyAddedTokenIds = new List<string>();

        try
        {
            string jsonResponse = await _httpClient.GetStringAsync(endpoint);
            
            //debugging the raw shape
            Console.WriteLine("DEBUGGING RAW API RESPONSE:");
            Console.WriteLine(jsonResponse.Substring(0, Math.Min(jsonResponse.Length, 500)));
            
            var topEvents = JsonSerializer.Deserialize<List<GammaEventDto>>(jsonResponse, _options);    
            
            if (topEvents == null)
            {
                Console.WriteLine("API didn't return any markets.");
                return;
            }

            foreach (var marketEvent in topEvents)
            {
                if (string.IsNullOrWhiteSpace(marketEvent.Id)) continue; 
                if (_activeWorkers.ContainsKey(marketEvent.Id)) continue;

                if (_activeWorkers.Count >= limit)
                {
                    bool evicted = await TryEvictColdestWorkerAsync();
                    if (!evicted) break; 
                }

                if (marketEvent.Markets == null) continue;
                
                var cleanYesTokens = new List<string>();
                var cleanFeeRates = new List<decimal>();

                try 
                {
                    foreach (var market in marketEvent.Markets.Where(m => m.Closed == false && m.Active)) 
                    {
                        if (string.IsNullOrWhiteSpace(market.ClobTokenIds)) continue;

                        var parsed = JsonSerializer.Deserialize<List<string>>(market.ClobTokenIds, _options);
                        if (parsed != null && parsed.Count > 0)
                        {
                            //isolate the YES token and add it to our clean list
                            cleanYesTokens.Add(parsed[0]); 
                            //conservative fallback: if the fee schedule is missing, assume the highest known taker rate (crypto, 7%) 
                            cleanFeeRates.Add(market.FeeSchedule?.Rate ?? 0.07m);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Skipping malformed market structure inside Event {marketEvent.Id}: {ex.Message}");
                    continue;
                }

                //a covering set requires at least 2 active outcomes. If it has less, skip it.
                if (cleanYesTokens.Count < 2) continue;
                if (marketEvent.NegRisk == false) continue;
                
                if(marketEvent.Ticker == "world-cup-winner") Console.WriteLine($"{marketEvent.Ticker} won!");
                var worker = new ClusterWorker(marketEvent.Id, cleanYesTokens, marketEvent.Ticker, cleanFeeRates);
                
                if (_activeWorkers.TryAdd(marketEvent.Id, worker))
                {
                    foreach (var tokenId in cleanYesTokens)
                    {
                        if (_activeAssetIds.TryAdd(tokenId, 0))
                        {
                            newlyAddedTokenIds.Add(tokenId);
                        }
                        _assetToEventMap[tokenId] = marketEvent.Id;
                    }
                    
                    worker.Start(_httpClient);
                    Console.WriteLine($"Started tracking: {marketEvent.Title} ({cleanYesTokens.Count} active YES outcomes)");
                }
            }
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Network error fetching active markets: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error in CheckToAddAsync: {ex}");
        }

        if (newlyAddedTokenIds.Count > 0)
        {
            Console.WriteLine($"[WS] Subscribing to {newlyAddedTokenIds.Count} newly discovered asset(s)...");
            await SubscribeToNewAssetsAsync(newlyAddedTokenIds);
        }

        LogAndResetActivityWindow();
    }

    //a period before a newly-added worker is eligible for eviction, so a market that just
    //started tracking isn't immediately rotated out before it's had a chance to show any activity.
    private static readonly TimeSpan MinWorkerAgeForEviction = TimeSpan.FromMinutes(2);

    private async Task<bool> TryEvictColdestWorkerAsync()
    {
        string? coldestId = null;
        int coldestActivity = int.MaxValue;
        DateTime now = DateTime.UtcNow;

        foreach (var kvp in _activeWorkers)
        {
            if (now - kvp.Value.StartedAtUtc < MinWorkerAgeForEviction) continue;

            int activity = kvp.Value.PeekActivity();
            if (activity < coldestActivity)
            {
                coldestActivity = activity;
                coldestId = kvp.Key;
            }
        }

        if (coldestId == null) return false;

        if (_activeWorkers.TryRemove(coldestId, out ClusterWorker workerToEvict))
        {
            await workerToEvict.StopAsync();
            await CleanupWorkerAssetsAsync(coldestId);
            Console.WriteLine($"[ROTATE] Evicted '{workerToEvict.EventName}' (0 book-changing messages this window) to make room for a hotter market.");
            return true;
        }

        return false;
    }

    // Shared by CheckToRemoveAsync and TryEvictColdestWorkerAsync: drops an event's tokens
    // from the local bookkeeping and pushes a live unsubscribe so we don't stay subscribed
    // to dead/rotated-out tokens forever.
    private async Task CleanupWorkerAssetsAsync(string eventId)
    {
        var removedTokenIds = new List<string>();
        foreach (var assetEntry in _assetToEventMap.Where(x => x.Value == eventId))
        {
            _assetToEventMap.TryRemove(assetEntry.Key, out _);
            if (_activeAssetIds.TryRemove(assetEntry.Key, out _))
            {
                removedTokenIds.Add(assetEntry.Key);
            }
        }

        if (removedTokenIds.Count > 0)
        {
            await UnsubscribeFromAssetsAsync(removedTokenIds);
        }
    }

    //logs the most active markets this polling window
    private void LogAndResetActivityWindow()
    {
        var ranked = _activeWorkers.Values
            .Select(w => (w.EventName, Activity: w.PeekActivity()))
            .OrderByDescending(x => x.Activity)
            .Take(5)
            .Where(x => x.Activity > 0)
            .ToList();

        if (ranked.Count > 0)
        {
            string summary = string.Join(" | ", ranked.Select(r => $"{r.EventName} ({r.Activity})"));
            Console.WriteLine($"[HOT] Most active this window: {summary}");
        }
        
        int totalArbs = _activeWorkers.Values.Sum(w => w.GetArbCount());

        Console.WriteLine($"[STATUS] {DateTime.Now:HH:mm:ss} | Total Arbs: {totalArbs}");

        foreach (var worker in _activeWorkers.Values)
        {
            worker.ConsumeActivity();
        }
    }

    public async Task CheckToRemoveAsync()
    {
        foreach (var trackedId in _activeWorkers.Keys)
        {
            string endpoint = $"events/{trackedId}";

            try
            {
                string jsonResponse = await _httpClient.GetStringAsync(endpoint);
                
                GammaEventDto eventStatus = JsonSerializer.Deserialize<GammaEventDto>(jsonResponse, _options);

                if (eventStatus != null)
                {
                    if (!eventStatus.Active || eventStatus.Closed)
                    {
                        Console.WriteLine($"Event {trackedId} is no longer active. Initiating shutdown...");
                        
                        if (_activeWorkers.TryRemove(trackedId, out ClusterWorker workerToStop))
                        {
                            await workerToStop.StopAsync();
                            await CleanupWorkerAssetsAsync(trackedId);
                            Console.WriteLine($"Successfully removed and shut down {trackedId}.");
                        }
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"Network error checking status for {trackedId}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error during removal check for {trackedId}: {ex}");
            }
        }
    }
    
    private async Task RouteMessageElementAsync(JsonElement element)
    {
        if (element.TryGetProperty("asset_id", out JsonElement idElement))
        {
            //"book" messages: asset_id lives at the top level.
            string assetId = idElement.GetString();
            if (assetId != null && _assetToEventMap.TryGetValue(assetId, out string eventId) && _activeWorkers.TryGetValue(eventId, out var worker))
            {
                //GetRawText() extracts just this individual object out of the array as a JSON string
                await worker.EnqueueDataAsync(element.GetRawText());
            }
        }
        else if (element.TryGetProperty("price_changes", out JsonElement changesElement) &&
                 changesElement.ValueKind == JsonValueKind.Array)
        {
            //"price_change" messages
            HashSet<string> routedEventIds = null;
            foreach (var change in changesElement.EnumerateArray())
            {
                if (change.TryGetProperty("asset_id", out var changeAssetIdEl))
                {
                    string changeAssetId = changeAssetIdEl.GetString();
                    if (changeAssetId != null && _assetToEventMap.TryGetValue(changeAssetId, out string evId))
                    {
                        (routedEventIds ??= new HashSet<string>()).Add(evId);
                    }
                }
            }

            if (routedEventIds != null)
            {
                foreach (var evId in routedEventIds)
                {
                    if (_activeWorkers.TryGetValue(evId, out var worker))
                    {
                        await worker.EnqueueDataAsync(element.GetRawText());
                    }
                }
            }
        }
    }
    
    public async Task StopAllAsync()
    {
        Console.WriteLine("[SHUTDOWN] Initiating graceful shutdown of all active workers...");
    
        //stop the global network manager loops
        _managerCts.Cancel();

        //safely close the global WebSocket
        if (_globalWebSocket != null && _globalWebSocket.State == WebSocketState.Open)
        {
            try
            {
                await _globalWebSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, 
                    "System shutting down", 
                    CancellationToken.None); //use None here so the close isn't aborted by our own CTS
            }
            catch { /* Ignore errors during forced closure */ }
        }

        //await all cluster workers to flush their CSVs and exit their loops
        var shutdownTasks = new List<Task>();
        foreach (var kvp in _activeWorkers)
        {
            shutdownTasks.Add(kvp.Value.StopAsync());
        }

        if (shutdownTasks.Count > 0)
        {
            Console.WriteLine($"[SHUTDOWN] Waiting for {shutdownTasks.Count} workers to flush to disk...");
            await Task.WhenAll(shutdownTasks); 
        }
    
        _activeWorkers.Clear();
        Console.WriteLine("[SHUTDOWN] All workers terminated safely.");
    }

    public void printActiveEvents()
    {
        foreach (string cluster in _activeWorkers.Keys)
        {
            Console.WriteLine(cluster);
        }
    }
}