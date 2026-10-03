//Airscrach's Another Fucking C# Steamworks Implementation Implementation V2.8
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using Steamworks;
using System;
using System.Linq;
using System.IO;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Buffers;

public class SteamNetwork : MonoBehaviour
{
    [SerializeField] uint GAMEID;
    [SerializeField] int tickRate = 60;
    [SerializeField] bool logNonErrors, logAsErrors, fallbackGAMEID;
    void Start() {
        DontDestroyOnLoad(gameObject); //keep the object alive
        int attempts = 0; bool ok = false;
        while(!ok){
            ok = SteamGlobal.StartSession(fallbackGAMEID ? 480 : GAMEID, tickRate,logNonErrors, logAsErrors);
            attempts++;
            if(attempts > 10){
                Debug.LogError("Failed to start steam session, player is probably not logged in");
                onSteamSetup?.Invoke(false);
                return;
            }
        }
        onSteamSetup?.Invoke(true);
    }
    void OnApplicationQuit(){ SteamClient.Shutdown(); }
    public delegate void OnSteamSetup(bool success);
    public event OnSteamSetup onSteamSetup;
}
public static class SteamGlobal{
    static uint GAMEID; //IN DARKNESS KEY (2595000), PARTYPACK1 (4225970)
    public static SteamId steamID, hostID;
    public static string playerName;
    static InternalUser[] lobbyPlayersArray; //Players in the lobby
    public static bool isHost = false;
    static bool UnityConsoleDebugging, UnityConsoleDebuggingAsError;
    public static Steamworks.Data.Lobby thisLobby;
    static SteamNetFrom netReciver;
    static bool setup = false, lobbySetup = false, Ticking = true, rdxSet = false;
    static int tickMS;
    static uint tick = 0;
    static float realtimeDeltaPlusX = 0; //this should never be set for the host
    /// <summary>Offset between this client's Time.realtimeSinceStartup and the host's.
    /// Convert local time to host time: localTime - realtimeOffset
    /// Convert host time to local time: hostTime + realtimeOffset
    /// Always 0 on the host.</summary>
    public static float realtimeOffset => realtimeDeltaPlusX;
    static List<UserIcon> icons = new List<UserIcon>();
    static byte nextClientId = 0, myClientId = 0;
    static uint localNetIdCounter = 0;
    static readonly ushort INTERNAL_HEADER_SPACE = 8, NETWORK_BUFFER_SIZE = 1024;
    static ConcurrentDictionary<ushort, OnDataRecived> dataHandlers = new ConcurrentDictionary<ushort, OnDataRecived>();
    public delegate void OnDataRecived(object data, SteamId id);
    public static event LobbyUpdate OnLobbyUpdate;
    public delegate void LobbyUpdate(LobbyData data);
    public static event ClientPlayerSpawned OnClientPlayerSpawned;
    public delegate void ClientPlayerSpawned(SteamId id, GameObject player);
    public static event NetTickEventHandler OnNetTick;
    public delegate void NetTickEventHandler(uint syncTick);

    //========================
    public static bool StartSession(uint gameid, int tps, bool logNonErrors, bool logsAsErrors){
        if(!setup){ //this is called when we first load the game
            try{
                GAMEID = gameid;
                UnityConsoleDebugging = logNonErrors;
                UnityConsoleDebuggingAsError = logsAsErrors;
                SteamClient.Init(GAMEID);
                setup = true;
                steamID = SteamClient.SteamId;
                playerName = SteamClient.Name;
                LobbyInviteListener();
                LobbyJoinListener();
                LobbyLeaveListenSetup();
                netReciver = SteamNetworkingSockets.CreateRelaySocket<SteamNetFrom>();
                ReciveTick();
                tickMS = 1000 / tps;
                NetworkTick();
                lobbyPlayersArray = new InternalUser[0];
            }
            catch (Exception e){
                Debug.LogError($"Steam Setup Error: {e.Message}");
                return false;
            }
            return true;
        }
        return true;
    }
    static async Task ReciveTick(){
        while(true){
            await Task.Delay(1);
            netReciver.Tick();
            for(int i = 0; i < lobbyPlayersArray.Length; i++){
                if(lobbyPlayersArray[i].connection != null){
                    lobbyPlayersArray[i].connection.Tick();
                }
            }
        }
    }
    public static void SetRichPresence(SteamRichPresence key, string value){
        //status, connect, steam_display, steam_player_group, steam_player_group_size
        string keystring = "";
        switch(key){
            case SteamRichPresence.Status: keystring = "status"; break;
            case SteamRichPresence.Connect: keystring = "connect"; break;
            case SteamRichPresence.SteamDisplay: keystring = "steam_display"; break;
            case SteamRichPresence.SteamPlayerGroup: keystring = "steam_player_group"; break;
            case SteamRichPresence.SteamPlayerGroupSize: keystring = "steam_player_group_size"; break;
        }
        SteamFriends.SetRichPresence(keystring, value);
    }
    public static void OpenSteamOverlay(SteamOverlayMode mode){
        string overlay = "";
        switch (mode){
            case SteamOverlayMode.Friends: overlay = "friends"; break;
            case SteamOverlayMode.Community: overlay = "community"; break;
            case SteamOverlayMode.Players: overlay = "players"; break;
            case SteamOverlayMode.Settings: overlay = "settings"; break;
            case SteamOverlayMode.GameGroup: overlay = "gamegroup"; break;
            case SteamOverlayMode.Stats: overlay = "stats"; break;
            case SteamOverlayMode.Achivements: overlay = "achievements"; break;
        }
        SteamFriends.OpenOverlay(overlay);
    }
    /// <summary>
    /// Regenerates the lobby array based on active members, preserving existing connections
    /// </summary>
    /// <param name="activeMembers">Array of currently active lobby members</param>
    static void RegenerateLobbyArray(Friend[] activeMembers) {
        if (activeMembers == null) throw new ArgumentNullException(nameof(activeMembers));
        // Store old connections for cleanup AFTER regeneration
        var oldConnections = lobbyPlayersArray?
            .Where(p => p?.connection != null && !activeMembers.Any(m => m.Id == p.Id))
            .Select(p => p.connection)
            .ToArray() ?? Array.Empty<SteamNetTo>();
        
        // Regenerate array first
        var foundPlayers = new List<InternalUser>(activeMembers.Length);
        var existingPlayers = lobbyPlayersArray?.Where(p => p != null).ToDictionary(p => p.Id, p => p) ?? new Dictionary<SteamId, InternalUser>();
        
        foreach (var member in activeMembers) {
            if (existingPlayers.TryGetValue(member.Id, out var existingPlayer)) {
                foundPlayers.Add(new InternalUser { Id = member.Id, friend = member, connection = existingPlayer.connection });
            } else {
                foundPlayers.Add(new InternalUser { Id = member.Id, friend = member, connection = null });
            }
        }
        
        lobbyPlayersArray = foundPlayers.ToArray();
        
        // Clean up old connections AFTER regeneration
        foreach (var connection in oldConnections) {
            connection?.Close();
        }
        DebugLog(PrettyPrintLobbyArray());
    }
    static void DisconnectAllPlayers(){
        if(lobbyPlayersArray == null){ return; }
        for(int i = 0; i < lobbyPlayersArray.Length; i++){
            if(lobbyPlayersArray[i].connection != null){
                lobbyPlayersArray[i].connection.Close();
                lobbyPlayersArray[i].connection = null;
            }
        }
    }
    public static (string, SteamId)[] GetLobbyPlayerInfo(){
        if(lobbyPlayersArray == null){ return null; }
        (string, SteamId)[] output = new (string, SteamId)[lobbyPlayersArray.Length];
        for(int i = 0; i < lobbyPlayersArray.Length; i++){
            output[i] = (lobbyPlayersArray[i].friend.Name, lobbyPlayersArray[i].Id);
        }
        return output;
    }
    static int GetPlayerIndex(SteamId id){
        for(int i = 0; i < lobbyPlayersArray.Length; i++){
            if(lobbyPlayersArray[i].Id == id){ return i; }
        }
        return -1;
    }
    public static string PrettyPrintLobbyArray(){
        string output = "CurrentLobby:";
        for(int i = 0; i < lobbyPlayersArray.Length; i++){
            output += $"\n{i}: {lobbyPlayersArray[i].friend.Name} ({lobbyPlayersArray[i].Id}) connected: {lobbyPlayersArray[i].connection != null}";
        }
        return output;
    }
    public static void InvokeSpawn(SteamId id, GameObject player){
        OnClientPlayerSpawned?.Invoke(id, player);
    }
    static void LobbyJoinListener(){
        SteamMatchmaking.OnLobbyMemberJoined += (Steamworks.Data.Lobby lobby, Friend friend) => {
            if(lobby.Id == thisLobby.Id){
                bool playerNotInLobby = GetPlayerIndex(friend.Id) == -1;
                RegenerateLobbyArray(lobby.Members.ToArray());
                if(playerNotInLobby){ //Process join
                    OnLobbyUpdate?.Invoke(new LobbyData(lobby, friend, friend.Id, LobbyEvent.PlayerJoined));
                    if(isHost){ //assign a client ID to the new player
                        int newPlayerIdx = GetPlayerIndex(friend.Id);
                        if(newPlayerIdx >= 0){
                            byte assignedId = nextClientId++;
                            lobbyPlayersArray[newPlayerIdx].clientId = assignedId;
                            byte[] idMsg = FormatBytes(2, ObjectToByteArray(assignedId));
                            SendReal(idMsg, newPlayerIdx, true);
                        }
                    }
                }
            }
        };
        SteamFriends.OnGameLobbyJoinRequested += (Steamworks.Data.Lobby lobby, SteamId id) => { //Process The join request
            OnLobbyUpdate?.Invoke(new LobbyData(lobby, id, LobbyEvent.SteamClientRequestedJoin));
        };
    }
    static void LobbyLeaveListenSetup(){
        SteamMatchmaking.OnLobbyMemberLeave += OnLeave;
        SteamMatchmaking.OnLobbyMemberKicked += (lobby, friend, kickedBy) => OnLeave(lobby, friend);
        SteamMatchmaking.OnLobbyMemberDisconnected += OnLeave;
    }
    static void OnLeave(Steamworks.Data.Lobby lobby, Friend friend){
        if(lobby.Id == thisLobby.Id){
            UpdateSocket(GetPlayerIndex(friend.Id), false);
            bool hostLeft = friend.Id == hostID;
            if(hostLeft){ 
                LeaveLobby(); //if the host leaves, close the lobby (handles disconnecting all players)
                //alert any gamelogic that the lobby was closed
                OnLobbyUpdate?.Invoke(new LobbyData(default, friend, friend.Id, LobbyEvent.LobbyClosed)); 
                return; 
            }
            RegenerateLobbyArray(lobby.Members.ToArray());
            OnLobbyUpdate?.Invoke(new LobbyData(lobby, friend, friend.Id , LobbyEvent.PlayerLeft));
            DebugLog($"Player: {friend.Name} left lobby, players: {lobbyPlayersArray.Length}");
        }
    }
    static void LobbyInviteListener(){
        SteamMatchmaking.OnLobbyInvite += (Friend friend, Steamworks.Data.Lobby lobby) => {
            OnLobbyUpdate?.Invoke(new LobbyData(lobby, friend, friend.Id, LobbyEvent.LobbyInviteRecived));
        };
    }
    static void UpdateSocket(int arrayIndex, bool active){
        if(arrayIndex < 0 || arrayIndex >= lobbyPlayersArray.Length) {
            DebugLog($"Invalid array index: {arrayIndex}, array length: {lobbyPlayersArray.Length}");
            return;
        }
        if(lobbyPlayersArray[arrayIndex] != null){
            if(active){
                if(lobbyPlayersArray[arrayIndex].connection == null) { 
                    lobbyPlayersArray[arrayIndex].connection = SteamNetworkingSockets.ConnectRelay<SteamNetTo>(lobbyPlayersArray[arrayIndex].Id); 
                    DebugLog($"Connected to {lobbyPlayersArray[arrayIndex].friend.Name}");
                } else {
                    DebugLog($"Attempted to connect to {lobbyPlayersArray[arrayIndex].friend.Name} but a connection was already found");
                }
            }
            else{
                if(lobbyPlayersArray[arrayIndex].connection != null){
                    lobbyPlayersArray[arrayIndex].connection.Close();
                    lobbyPlayersArray[arrayIndex].connection = null;
                    DebugLog($"Disconnected from {lobbyPlayersArray[arrayIndex].friend.Name}");
                } else{
                    DebugLog($"Attempted to disconnect from {lobbyPlayersArray[arrayIndex].friend.Name} but no connection was found");
                }
            }
        } else {
            DebugLog($"Attempted to update socket for player {arrayIndex} but found null");
        }
    }
    
    static async Task NetworkTick(){ while(Ticking){ await Task.Delay(tickMS); tick++; OnNetTick?.Invoke(tick); } } //this is the server tickrate, lower = more updates (16 is 60tps)
    public static async Task<Steamworks.Data.Lobby?> JoinLobbyFromID(SteamId id){
        Steamworks.Data.Lobby? lbby = await SteamMatchmaking.JoinLobbyAsync(id.Value);
        RoomEnter re = await lbby.Value.Join();
        if(re == RoomEnter.Success){
            thisLobby = lbby.Value;
            await thisLobby.Owner.RequestInfoAsync();
            hostID = thisLobby.Owner.Id;
            isHost = false; lobbySetup = true;
            DebugLog($"Joined lobby {thisLobby.Id}, lobby owner: {thisLobby.Owner.Name}, id: {thisLobby.Owner.Id}");
            await Task.Delay(1000); //wait for the lobby to update
            RegenerateLobbyArray(thisLobby.Members.ToArray());
            OnLobbyUpdate?.Invoke(new LobbyData(thisLobby, steamID, LobbyEvent.JoinedLobby));
            return thisLobby;
        }
        else{ Debug.LogError("Failed to join lobby"); thisLobby = default; return null;}
    }
    public static async Task CreateLobby(int maxPlayers, HostMode mode, Dictionary<string, string> data = null){
        if(!lobbySetup){
            var lb = await SteamMatchmaking.CreateLobbyAsync(maxPlayers);
            if (lb != null){
                thisLobby = lb.Value;
                if (mode == HostMode.Friends) { thisLobby.SetFriendsOnly(); }
                else if (mode == HostMode.Public) { thisLobby.SetPublic(); }
                else if (mode == HostMode.Private) { thisLobby.SetFriendsOnly(); thisLobby.SetJoinable(false); }
                lobbySetup = true; isHost = true;
                myClientId = nextClientId++;
                RegenerateLobbyArray(thisLobby.Members.ToArray());
                OnLobbyUpdate?.Invoke(new LobbyData(thisLobby, steamID, LobbyEvent.LobbyCreated));
                await UpdateLobbyData(data);
            }else{
                DebugLog("Failed to create lobby");
            }
        }else{ DebugLog("Attempted to create lobby while already in a lobby"); }
    }
    /// <summary>
    /// Update the data of the current lobby (Should be called every time we change scene)
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    public static async Task UpdateLobbyData(Dictionary<string, string> data = null){
        if(lobbySetup && isHost){
            //set the lobby data
            thisLobby.SetData("scene", SceneManager.GetActiveScene().buildIndex.ToString());
            thisLobby.SetData("host", playerName);
            if(data != null){
                foreach(KeyValuePair<string, string> entry in data){
                    thisLobby.SetData(entry.Key, entry.Value);
                }
            }
            OnLobbyUpdate?.Invoke(new LobbyData(thisLobby, steamID, LobbyEvent.LobbyDataUpdated));
        }
    }
    /// <summary>
    /// Get the data of the current lobby
    /// </summary>
    /// <param name="key">The key of the data</param>
    /// <returns>The data</returns>
    public static string QueryLobbyData(string key){
        if(lobbySetup){
            return thisLobby.GetData(key);
        }
        return null;
    }
    /// <summary>
    /// Leave the current lobby
    /// </summary>
    public static void LeaveLobby(){
        DisconnectAllPlayers();
        lobbyPlayersArray = new InternalUser[0];
        nextClientId = 0; myClientId = 0; localNetIdCounter = 0;
        if(lobbySetup) { thisLobby.Leave(); }
        netReciver.Close();
        netReciver = SteamNetworkingSockets.CreateRelaySocket<SteamNetFrom>();
        thisLobby = default; hostID = default;
        lobbySetup = false; isHost = false;
        rdxSet = false;
    }
    /// <summary>
    /// Get all public lobbies
    /// </summary>
    /// <param name="distance">The distance to search for lobbies</param>
    /// <param name="filters">Filters to apply to the search</param>
    /// <param name="requireOpenSlots">The amount of open slots required</param>
    /// <returns>An array of lobbies</returns>
    public static async Task<Steamworks.Data.Lobby[]> GetAllPublicLobbies(SteamLobbySearchDistance distance = SteamLobbySearchDistance.Close, Dictionary<string, string> filters = null, int requireOpenSlots = 0){
        try{
            Steamworks.Data.LobbyQuery list = new Steamworks.Data.LobbyQuery();
            if(distance == SteamLobbySearchDistance.Close){ list.FilterDistanceClose(); }
            else if(distance == SteamLobbySearchDistance.Far){ list.FilterDistanceFar(); }
            else if(distance == SteamLobbySearchDistance.Worldwide){ list.FilterDistanceWorldwide(); }
            if(filters != null){
                foreach(KeyValuePair<string, string> filter in filters){
                    list.WithKeyValue(filter.Key, filter.Value);
                }
            }
            if(requireOpenSlots > 0){ list.WithSlotsAvailable(requireOpenSlots); }
            return await list.RequestAsync();
        }
        catch{
            return null;
        }
    }
    /// <summary>
    /// Grant or Revoke an achivement
    /// </summary>
    /// <param name="name">Name of the achivement</param>
    /// <param name="grant">Give or Revoke</param>
    public static void GiveAchivement(string name, bool grant = true){
        IEnumerable<Steamworks.Data.Achievement> achivements = SteamUserStats.Achievements;
        Steamworks.Data.Achievement achivement = achivements.FirstOrDefault(x => x.Identifier == name);
        if(grant){ achivement.Trigger(); } else{ achivement.Clear(); }
    }
    /// <summary>
    /// Set progress on an achivement (for stats)
    /// </summary> <param name="name">Name of the achivement</param>
    /// <param name="progress">Progress to set (numerical)</param>
    public static void SetAchivementProgress(string name, int progress){
        SteamUserStats.SetStat(name, progress);
    }
    public static void AddAchivementProgress(string name, int progress){
        SteamUserStats.AddStat(name, progress);
    }
    /// <summary>
    /// sets a leaderboard score
    /// </summary>
    /// <param name="leaderboard">name of the leaderboard</param>
    /// <param name="score"></param>
    /// <returns>Did it succeed</returns>
    public static async Task<bool> SetLeaderboardScore(string leaderboard, int score){
        Steamworks.Data.Leaderboard? lb = await SteamUserStats.FindLeaderboardAsync(leaderboard);
        if(lb != null){
            Steamworks.Data.LeaderboardUpdate? entry = await lb.Value.SubmitScoreAsync(score);
            if(entry != null){ DebugLog($"Set leaderboard score: {entry.Value.NewGlobalRank} ({entry.Value.Score})"); return true; }
            else{ Debug.LogError("Failed to set leaderboard score"); return false; }
        }
        else{ Debug.LogError("Failed to find leaderboard"); return false; }
    }
    /// <summary>
    /// Get A Steam User Icon (This is cached)
    /// </summary>
    /// <param name="id">The Steam ID of the user</param>
    /// <returns>The User Icon</returns>
    public static async Task<Texture2D> GetUserIcon(SteamId id){
        UserIcon icon = icons.Find(x => x.id == id);
        if(icon == null){
            Steamworks.Data.Image? img = await SteamFriends.GetMediumAvatarAsync(id);
            if(img != null){
                var avatar = new Texture2D((int)img.Value.Width, (int)img.Value.Height, TextureFormat.ARGB32, false)
                { filterMode = FilterMode.Trilinear /* Set filter type, or else its really blury*/ };
                int imgWidth = (int)img.Value.Width, imgHeight = (int)img.Value.Height; // Flip image
                Steamworks.Data.Color p;
                for ( int x = 0; x < imgWidth; x++ ) {
                    for ( int y = 0; y < imgHeight; y++ ) {
                        p = img.Value.GetPixel(x, y);
                        avatar.SetPixel(x, imgHeight - y, new Color(p.r / 255.0f, p.g / 255.0f, p.b / 255.0f, p.a / 255.0f));
                    }
                    await Task.Delay(1); //reduce lag
                }
                avatar.Apply();
                return avatar;
            } else{Debug.LogError("Failed to get user icon"); return null; }
        } else{ return icon.icon; }
    }
    public static void ClearIconCache(){ icons.Clear(); }

    /// <summary>
    /// Returns a network ID guaranteed unique to this client.
    /// High byte = client ID (assigned by host), low 3 bytes = local incremental counter.
    /// No cross-client coordination required.
    /// </summary>
    /// <returns>uid</returns>
    public static uint GetNetID(){
        uint id = ((uint)myClientId << 24) | (localNetIdCounter & 0x00FFFFFFu);
        localNetIdCounter++;
        return id;
    }

    // Sending Functions =========================================================================================================
    // these functions are used to send data to other players and reserve some internal IDs for system messages
    public static bool SendAllData(ushort dataID, object data, bool reliable = true){
        if(!lobbySetup){ return false; }
        byte[] dataArr = FormatBytes((ushort)(dataID + INTERNAL_HEADER_SPACE), ObjectToByteArray(data));
        SendArrayAllPlayerSafe(dataArr);
        return true;
    }
    /// <summary>
    /// Send data to a specific player
    /// </summary>
    /// <param name="dataID"></param>
    /// <param name="data"></param>
    /// <param name="target">Host if left blank</param>
    /// <param name="reliable"></param>
    /// <returns></returns>
    public static bool SendDirectData(ushort dataID, object data, SteamId target = default, bool reliable = true){
        if(!lobbySetup){ return false; }
        byte[] dataArr = FormatBytes((ushort)(dataID + INTERNAL_HEADER_SPACE), ObjectToByteArray(data));
        if(target == default){ //send to host
            if(hostID == default){ Debug.LogError("No host ID provided for host send"); return false; }
            return SendReal(dataArr, GetPlayerIndex(hostID), reliable);
        } else {
            if(target == steamID){ Debug.LogError("Attempted to send message to self"); return false; }
            return SendReal(dataArr, GetPlayerIndex(target), reliable);
        }
    }
    /// <summary>
    /// Requests all clients to update their RDX (Ensure this is called before using any syncronized events)
    /// </summary>
    public static void UpdateRDXRequest(){
        if(!isHost || !lobbySetup){ Debug.LogError("Attempted to request RDX from non-host || not in lobby"); return; }
        byte[] dataArr = FormatBytes(0, ObjectToByteArray(Time.realtimeSinceStartup));
        SendArrayAllPlayerSafe(dataArr);
        
    }
    /// <summary>
    /// Send a syncronized event to all clients (will also perform the event on the host)
    /// </summary>
    /// <param name="runIn">Time in seconds to run the event</param>
    /// <param name="dataID">The ID of the event</param>
    /// <param name="data">The data to send</param>
    /// <param name="target">The target player, if left blank, it will be sent to all players</param>
    public static bool SendSyncEvent(float runIn, ushort dataID, object data, SteamId target = default){
        if(!isHost || !lobbySetup){ Debug.LogError("Attempted to send sync event from non-host || not in lobby"); return false; }
        if(runIn < 0){ Debug.LogError("Attempted to send sync event in the past"); return false; }
        byte[] dataArr = FormatBytes((ushort)(dataID + INTERNAL_HEADER_SPACE), ObjectToByteArray(data));
        InternalSyncedEvent ev = new InternalSyncedEvent{ rdx = Time.realtimeSinceStartup, timeToEvent = runIn, eventToProcess = dataArr };
        byte[] evArr = FormatBytes(1, ObjectToByteArray(ev));
        if(target == default){
            SendArrayAllPlayerSafe(evArr);
        } else {
            if(target == steamID){ Debug.LogError("Attempted to send message to self"); return false; }
            return SendReal(evArr, GetPlayerIndex(target), true);
        }
        //if target was default, start the call on the host
        if(target == default){ ProcessPacketIn((int)(runIn * 1000), dataArr, steamID); }
        return true;
    }
    static void SendArrayAllPlayerSafe(byte[] data){
        ReadOnlySpan<InternalUser> players = lobbyPlayersArray.AsSpan();
        foreach (ref readonly var player in players) {
            if (player == null) {  DebugLog("Player is null (BAD)");  continue;  }
            if (player.Id != steamID) { 
                SendReal(data, Array.IndexOf(lobbyPlayersArray, player), true); 
            }
        }
    }

    // ============================================================================================================================
    // Internal Sending Functions =================================================================================================
    static bool SendReal(byte[] content, int recipientIdx, bool reliable){
        if(recipientIdx == -1 || recipientIdx > lobbyPlayersArray.Length){ Debug.LogError($"Failed to find player to send message to: {recipientIdx}"); return false; }
        if(lobbyPlayersArray[recipientIdx].connection != null){
            return lobbyPlayersArray[recipientIdx].connection.Transmit(content, reliable);
        }else{
            SteamNetTo newConnection = SteamNetworkingSockets.ConnectRelay<SteamNetTo>(lobbyPlayersArray[recipientIdx].Id);
            if(newConnection != null){
                lobbyPlayersArray[recipientIdx].connection = newConnection;
                //PrettyPrintLobbyArray();
                return newConnection.Transmit(content, reliable);
            }else{
                Debug.LogError($"Failed to send message to {lobbyPlayersArray[recipientIdx].friend.Name}");
                return false;
            }
        }
    }
    static void HandleMessage(SteamId steamid, byte[] data) {
         try {
        ushort index = BitConverter.ToUInt16(data, 0);
        int contentLength = data.Length - 2;
        
        // Rent a buffer from the shared ArrayPool
        byte[] rentedArray = ArrayPool<byte>.Shared.Rent(contentLength);
        
        try {
            // Copy content to the rented array
            Buffer.BlockCopy(data, 2, rentedArray, 0, contentLength);
            
            if (index < INTERNAL_HEADER_SPACE) {
                // System message
                object deserializedObject = ByteArrayToObject(rentedArray, 0, contentLength);
                InternalMessageHandler(index, deserializedObject, steamid);
            } 
            else if (dataHandlers.TryGetValue((ushort)(index - INTERNAL_HEADER_SPACE), out OnDataRecived handler)) {
                // User handler
                object deserializedObject = ByteArrayToObject(rentedArray, 0, contentLength);
                handler.Invoke(deserializedObject, steamid);
            }
        }
        finally {
            // Return the buffer to the pool when done
            ArrayPool<byte>.Shared.Return(rentedArray);
        }
    }
    catch (Exception e) {
        Debug.LogError($"Error Formatting data: {e.Message} ({e.StackTrace})");
    }
    }
    // ============================================================================================================================
    static void InternalMessageHandler(ushort id, object data, SteamId sender){
        //Internal Messages
        // 0 = RealtimeDelta Update
        // 1 = SyncronizedEvent
        // 2 = ClientID Assignment (host -> client)
        if(id == 0){
            if(!isHost){
                realtimeDeltaPlusX = Time.realtimeSinceStartup - (float)data;
                rdxSet = true;
            }else{
                DebugLog("Host recieved realtime delta update");
            }
        }else if(id == 1){
            if(isHost || !rdxSet){ Debug.LogError("Either attempting to call sync event on host or rdx not set"); return; }
            InternalSyncedEvent ev = (InternalSyncedEvent)data;
            float timeToEvent = ev.rdx + ev.timeToEvent + realtimeDeltaPlusX - Time.realtimeSinceStartup;
            if(timeToEvent < 0){ 
                Debug.LogError("Event was in the past"); return;
            }else{
                ProcessPacketIn((int)(timeToEvent * 1000), ev.eventToProcess, sender);
                //update the rdx
                realtimeDeltaPlusX = Time.realtimeSinceStartup - ev.rdx;
            }
        } else if(id == 2){
            if(isHost){ DebugLog("Host received client ID assignment (unexpected)"); return; }
            myClientId = (byte)data;
            localNetIdCounter = 0;
            DebugLog($"Received client ID: {myClientId}");
        }
    }

    static async Task ProcessPacketIn(int fireInMs,byte[] data, SteamId id){
        await Task.Delay(fireInMs);
        HandleMessage(id, data);
    }

    /// <summary>
    /// Listen to data
    /// </summary>
    /// <param name="ID">The Event ID</param>
    /// <param name="handler">The Function Data Should be Passed To</param>
    public static void Bind(ushort ID, OnDataRecived handler) {
        if(!dataHandlers.ContainsKey(ID)){ dataHandlers[ID] = handler; }
        else{ dataHandlers[ID] += handler; }
    }
    /// <summary>
    /// Stop listening to data
    /// </summary>
    /// <param name="ID">The Event ID</param>
    /// <param name="handler">The Function Data Was Passed To</param>
    public static void UnBind(ushort ID, OnDataRecived handler){
        if(dataHandlers.ContainsKey(ID)){ 
            dataHandlers[ID] -= handler;
            if(dataHandlers[ID] == null){ dataHandlers.Remove(ID, out _); }
        }
    }
    class UserIcon{
        public SteamId id;
        public Texture2D icon;
        public UserIcon(SteamId id, Texture2D icon){
            this.id = id;
            this.icon = icon;
        }
    }
    static byte[] FormatBytes(ushort dataID, byte[] data){
        byte[] dataArr = new byte[2 + data.Length];
        BitConverter.TryWriteBytes(dataArr.AsSpan(0, 2), dataID);
        data.CopyTo(dataArr, 2);
        return dataArr;
    }
    static byte[] ObjectToByteArray(object obj) {
        try {
            return NetCodec.Encode(obj); // only NetCodec's listed message types (see NetCodec)
        }
        catch (Exception e) {
            Debug.LogError($"Error converting object to byte array: {e.Message}");
            return Array.Empty<byte>(); // Return empty array instead of null
        }
    }
    //Byte array to object
    static object ByteArrayToObject(byte[] arrBytes, int offset, int length) {
    try {
        return NetCodec.Decode(arrBytes, offset, length); // never builds a type outside NetCodec's list
    }
    catch(Exception e) {
        Debug.LogError($"Error converting byte array to object: {e.Message}");
        return null;
    }
}
    /// <summary>
    /// Get the current scene of the server
    /// </summary>
    /// <returns></returns>
    public static int GetServerScene(){
        try{ return int.Parse(thisLobby.GetData("scene")); }
        catch{ return -1; }
    }
    static void DebugLog(string message){
        if(UnityConsoleDebugging){
            if(UnityConsoleDebuggingAsError){ Debug.LogError(message); }
            else{ Debug.Log(message); }
        }
    }
    class SteamNetFrom : SocketManager {
        public override void OnConnecting(Steamworks.Data.Connection connection, Steamworks.Data.ConnectionInfo info) {
            connection.Accept();
            DebugLog($"[Input] Accepted connection from {info.Identity}");
        }
        public override void OnConnected(Steamworks.Data.Connection connection, Steamworks.Data.ConnectionInfo info) {
            base.OnConnected(connection, info);
            //DebugLog($"[Input] Connected to {info.Identity}");
        }
        public override void OnDisconnected(Steamworks.Data.Connection connection, Steamworks.Data.ConnectionInfo info) {
            base.OnDisconnected(connection, info);
            DebugLog($"[Input] Disconnected from {info.Identity}, reason: {info.EndReason}");
            
            // Clean up the corresponding outgoing connection if it exists
            SteamId disconnectedId = info.Identity.SteamId;
            int playerIdx = GetPlayerIndex(disconnectedId);
            if (playerIdx >= 0 && playerIdx < lobbyPlayersArray.Length) {
                if (lobbyPlayersArray[playerIdx].connection != null) {
                    lobbyPlayersArray[playerIdx].connection.Close();
                    lobbyPlayersArray[playerIdx].connection = null;
                    DebugLog($"[Input] Cleaned up outgoing connection for {disconnectedId}");
                }
            }
        }
        public override void OnMessage(Steamworks.Data.Connection connection, Steamworks.Data.NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel) {
            base.OnMessage(connection, identity, data, size, messageNum, recvTime, channel); 
            byte[] dataArr = new byte[size]; //convert the data to a byte array
            System.Runtime.InteropServices.Marshal.Copy(data, dataArr, 0, size);
            HandleMessage(identity.SteamId, dataArr);
        }
        public void Tick(){
            Receive(NETWORK_BUFFER_SIZE);
        }
    }
    class SteamNetTo : ConnectionManager {
        public override void OnConnecting( Steamworks.Data.ConnectionInfo info ) {
            base.OnConnecting(info);
            //DebugLog($"[Output] Connecting to {info.State}");
        }
        public override void OnConnected( Steamworks.Data.ConnectionInfo info ) {
            base.OnConnected(info);
            //DebugLog($"[Output] Connected to {info.State}");
        }
        public override void OnDisconnected( Steamworks.Data.ConnectionInfo info ) {
            base.OnDisconnected(info);
            DebugLog($"[Output] Disconnected from {info.Identity}, reason: {info.EndReason}");
            
            // Find and null out this connection in the lobby array
            // The connection will be recreated on next send attempt
            SteamId disconnectedId = info.Identity.SteamId;
            int playerIdx = GetPlayerIndex(disconnectedId);
            if (playerIdx >= 0 && playerIdx < lobbyPlayersArray.Length) {
                if (lobbyPlayersArray[playerIdx].connection == this) {
                    lobbyPlayersArray[playerIdx].connection = null;
                    DebugLog($"[Output] Cleared connection reference for {disconnectedId}");
                }
            }
        }
        public override void OnMessage( IntPtr data, int size, long messageNum, long recvTime, int channel ){
            base.OnMessage(data, size, messageNum, recvTime, channel);
            DebugLog($"[Output] Received message");
        }
        public bool Transmit(byte[] data, bool reliable){
            IntPtr ptr = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, ptr, data.Length);
            Result output = Connection.SendMessage(ptr, data.Length, reliable ? Steamworks.Data.SendType.Reliable : Steamworks.Data.SendType.Unreliable);
            Marshal.FreeHGlobal(ptr);
            return output == Result.OK;
        }
        public void Tick(){
            Receive(NETWORK_BUFFER_SIZE);
        }
    }
    public static bool IsUDPNewer(uint A, uint B){
        return (int)(A - B) > 0;
    }
    class InternalUser{
        public SteamId Id;
        public Friend friend;
        public SteamNetTo connection;
        public byte clientId;
    }
    [System.Serializable]
    class InternalSyncedEvent{
        public float rdx, timeToEvent;
        public byte[] eventToProcess;
    }
}
public enum HostMode{
    Public, Friends, Private
}
public enum LobbyEvent{
    PlayerJoined, PlayerLeft, LobbyClosed, SteamClientRequestedJoin, LobbyInviteRecived, LobbyCreated, JoinedLobby, LobbyDataUpdated
}
public enum SteamLobbySearchDistance{
    Close, Far, Worldwide
}
public enum SteamOverlayMode{
    Friends, Community, Players, Settings, GameGroup, Stats, Achivements
}
public enum SteamRichPresence{
    Status, Connect, SteamDisplay, SteamPlayerGroup, SteamPlayerGroupSize
}
public class LobbyData{
    public Friend dataFriend;
    public SteamId dataUserID;
    public Steamworks.Data.Lobby dataLobby;
    public LobbyEvent lobbyEvent;
    public LobbyData(Steamworks.Data.Lobby lobby, Friend friend, SteamId friendID, LobbyEvent lobbyEvent){
        this.dataFriend = friend;
        this.lobbyEvent = lobbyEvent;
        this.dataLobby = lobby;
        this.dataUserID = friendID;
    }
    public LobbyData(Steamworks.Data.Lobby lobby, SteamId id, LobbyEvent lobbyEvent){
        this.dataUserID = id;
        this.lobbyEvent = lobbyEvent;
        this.dataLobby = lobby;
    }

}