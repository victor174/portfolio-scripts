using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

using Unity.Services.Authentication;
using Unity.Services.Economy;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;

using MModels = Unity.Services.Matchmaker.Models;

// Project-specific namespace/types (kept as-is for portfolio sample).
using Taitiko;

/// <summary>
/// Private lobby manager using Unity Lobby Service.
/// 
/// Responsibilities:
/// - Create a private lobby (host)
/// - Join a lobby by code (client)
/// - Subscribe to lobby events (player join/leave, lobby data changes)
/// - Maintain a heartbeat while hosting
/// - Update lobby data when the game server is ready (GameServer payload)
/// - Drive basic UI transitions (project-specific UI managers)
/// 
/// Notes:
/// - Some dependencies are project-specific (Database, PrivateLobbyLocalManager, MenuController, etc.).
/// - Certain identifiers (scene names, economy currency IDs, map IDs) were anonymized for portfolio.
/// </summary>
public class PrivateLobbyManager : MonoBehaviour
{
    public static PrivateLobbyManager singleton;

    [Header("Lobby State")]
    public string myLobbyCode;
    public int playersInLobby;
    public string lobbyId;
    public Lobby lobby;

    [Header("Lobby Events")]
    public ILobbyEvents m_LobbyEvents;

    [Header("UI / Prefabs")]
    public GameObject playerPrefab;

    // Unused in this snippet but kept as-is (original field).
    public List<Unity.Services.Lobbies.Models.Player> jugadores;

    public void Start()
    {
        // Simple singleton pattern.
        if (singleton == null)
        {
            singleton = this;
        }
        else
        {
            Destroy(this);
        }
    }

    private void ResetLobbyStatus()
    {
        myLobbyCode = null;
        playersInLobby = 0;
        lobbyId = null;
        lobby = null;
    }

    public async void CreatePrivateParty()
    {
        await CreateLobbyWithHeartbeatAsync();
    }

    public void goToActualLobby()
    {
        if (!string.IsNullOrEmpty(lobbyId))
        {
            // Project-specific UI view switching.
            PrivateLobbyLocalManager.singleton.createView.Hide();
            PrivateLobbyLocalManager.singleton.inGameView.Show();
            RefreshVisual();
        }
    }

    public void CopyText()
    {
        GUIUtility.systemCopyBuffer = PrivateLobbyLocalManager.singleton.codeText.text;
        Debug.Log("Copied lobby code: " + PrivateLobbyLocalManager.singleton.codeText.text);
    }

    /// <summary>
    /// Host flow:
    /// - Creates a private lobby
    /// - Subscribes to lobby events
    /// - Starts a heartbeat coroutine to keep lobby alive
    /// - Updates initial lobby data (e.g. SelectedMap)
    /// - Updates UI to show the lobby screen
    /// </summary>
    async Task<Lobby> CreateLobbyWithHeartbeatAsync()
    {
        Debug.Log("Creating new lobby!");
        playersInLobby = 0;

        // Lobby name includes host player id (safe for internal use).
        string lobbyName = "lobby-" + AuthenticationService.Instance.PlayerId;

        int maxPlayers = 3;

        CreateLobbyOptions options = new CreateLobbyOptions
        {
            IsPrivate = true,
            Player = GetPlayer()
        };

        Debug.Log("Pre Lobby call");
        lobby = await LobbyService.Instance.CreateLobbyAsync(lobbyName, maxPlayers, options);

        // Subscribe to lobby events (changes, join/leave, connection state).
        var callbacks = new LobbyEventCallbacks();
        callbacks.LobbyChanged += OnLobbyChanged;
        callbacks.KickedFromLobby += OnKickedFromLobby;
        callbacks.LobbyEventConnectionStateChanged += OnLobbyEventConnectionStateChanged;
        callbacks.PlayerJoined += OnLobbyPlayerJoined;
        callbacks.PlayerLeft += OnLobbyPlayerLeft;

        try
        {
            m_LobbyEvents = await Lobbies.Instance.SubscribeToLobbyEventsAsync(lobby.Id, callbacks);
        }
        catch (LobbyServiceException ex)
        {
            switch (ex.Reason)
            {
                case LobbyExceptionReason.AlreadySubscribedToLobby:
                    Debug.LogWarning($"Already subscribed to lobby[{lobby.Id}]. No need to subscribe again. Exception: {ex.Message}");
                    break;

                case LobbyExceptionReason.SubscriptionToLobbyLostWhileBusy:
                    Debug.LogError($"Lobby event subscription lost while subscribing. Exception: {ex.Message}");
                    throw;

                case LobbyExceptionReason.LobbyEventServiceConnectionError:
                    Debug.LogError($"Failed to connect to lobby events. Exception: {ex.Message}");
                    throw;

                default:
                    throw;
            }
        }

        Debug.Log("Lobby created with:");
        Debug.Log("id: " + lobby.Id + " code: " + lobby.LobbyCode);

        lobbyId = lobby.Id;
        myLobbyCode = lobby.LobbyCode;

        // Heartbeat to prevent the lobby from expiring while the host is active.
        StartCoroutine(HeartbeatLobbyCoroutine(15));

        if (await UpdateLobby())
        {
            Debug.Log("Lobby updated!");
            RefreshVisual();
        }
        else
        {
            Debug.Log("Lobby not updated :(");
        }

        // Project-specific UI update.
        PrivateLobbyLocalManager.singleton.createView.Hide();
        PrivateLobbyLocalManager.singleton.inGameView.Show();
        PrivateLobbyLocalManager.singleton.codeText.text = lobby.LobbyCode;

        // Project-specific game selection flag.
        Database.UserGameSelection.gameType = Taitiko.DB.GameType.Private;

        return lobby;
    }

    private void OnLobbyPlayerJoined(List<LobbyPlayerJoined> players)
    {
        Debug.Log("OnLobbyPlayerJoined()");
        playersInLobby = lobby.Players.Count;
        RefreshVisual();
    }

    private void OnLobbyPlayerLeft(List<int> players)
    {
        playersInLobby = lobby.Players.Count;
        RefreshVisual();
    }

    /// <summary>
    /// Called when lobby data changes. This is where we react to "GameServer" being set.
    /// Expected payload format (project-specific convention):
    /// "IP$PORT$MATCH_ID"
    /// </summary>
    private void OnLobbyChanged(ILobbyChanges changes)
    {
        Debug.Log("[LOBBY] ON LOBBY CHANGED");
        Debug.Log(changes);
        Debug.Log(changes.Data.Added);
        Debug.Log(changes.Data.Changed);

        // Apply patch to local lobby state.
        changes.ApplyToLobby(lobby);

        if (changes.LobbyDeleted)
        {
            ResetLobbyStatus();

            // Project-specific UI
            PrivateLobbyLocalManager.singleton.inGameView.Hide();
            PrivateLobbyLocalManager.singleton.createView.Show();

            Database.UserGameSelection.gameType = Taitiko.DB.GameType.Public;
        }
        else if (changes.Data.Added != false || changes.Data.Changed != false)
        {
            DataObject serverData;
            lobby.Data.TryGetValue("GameServer", out serverData);

            // Convention: server info is stored in S2 index.
            if (serverData.Index == DataObject.IndexOptions.S2)
            {
                if (serverData.Value != null)
                {
                    // Example payload: "100.100.100.100$4500$match-id"
                    string[] data = serverData.Value.Split("$");
                    Debug.Log(data[0]);
                    Debug.Log(UInt16.Parse(data[1]));

                    // Push server allocation into match services DB (project-specific).
                    MatchmakerManager.singleton.servicesDB.match = new TaitikoUS.lastMatch();
                    MatchmakerManager.singleton.servicesDB.match.matchServer = data[0];
                    MatchmakerManager.singleton.servicesDB.match.matchPort = UInt16.Parse(data[1]);
                    MatchmakerManager.singleton.servicesDB.match.matchId = data[2];

                    Database.UserGameSelection.gameType = Taitiko.DB.GameType.Private;

                    // Anonymized scene name for portfolio.
                    SceneManager.LoadScene("EXAMPLE_GAME_SCENE");
                }
            }
        }

        RefreshVisual();
    }

    /// <summary>
    /// Refreshes lobby UI elements:
    /// - Host-only controls (code container, start button)
    /// - Player list display (instantiating player prefab entries)
    /// </summary>
    public void RefreshVisual()
    {
        if (lobby == null)
        {
            Debug.Log("[LOBBY EMPTY, ABORTING VISUAL REFRESH]");
            return;
        }

        bool isHost = lobby.HostId == AuthenticationService.Instance.PlayerId;

        PrivateLobbyLocalManager.singleton.codeContainer.SetActive(isHost);
        PrivateLobbyLocalManager.singleton.codeText.text = lobby.LobbyCode;
        PrivateLobbyLocalManager.singleton.startMatchButton.SetActive(isHost);

        Debug.Log("[LOBBY PLAYERS]");
        Debug.Log(JsonUtility.ToJson(lobby.Players));

        // Clear previous UI elements
        foreach (Transform child in PrivateLobbyLocalManager.singleton.container)
        {
            Destroy(child.gameObject);
        }

        // Rebuild player list UI
        for (int a = 0; a < lobby.Players.Count; a++)
        {
            GameObject nuevo = Instantiate(playerPrefab, PrivateLobbyLocalManager.singleton.container);
            PrivateLobbyPlayerElementManager controller = nuevo.GetComponent<PrivateLobbyPlayerElementManager>();

            controller.NameText.text = lobby.Players[a].Data["Name"].Value;
            controller.WinsText.text = lobby.Players[a].Data["Win"].Value;
            controller.CheckpointsText.text = lobby.Players[a].Data["Checkpoint"].Value;
            controller.DashesWinsText.text = lobby.Players[a].Data["Dash"].Value;
            controller.RabbitsText.text = lobby.Players[a].Data["Rabbit"].Value;
            controller.userId = lobby.Players[a].Id;

            Debug.Log(lobby.Players[a].Data["Name"].Value);
            Debug.Log(lobby.Players[a].Data["Skin"].Value);
        }
    }

    public async void ExitLobby()
    {
        try
        {
            await LobbyService.Instance.RemovePlayerAsync(lobbyId, AuthenticationService.Instance.PlayerId);
        }
        catch (LobbyServiceException e)
        {
            // Project-specific error popup
            PrivateLobbyLocalManager.singleton.notFoundPopUpText.text = e.Message;
            PrivateLobbyLocalManager.singleton.notFoundPopUp.Show();
        }

        ResetLobbyStatus();

        Database.UserGameSelection.gameType = Taitiko.DB.GameType.Public;
        MenuController.singleton.OnBackBTN();
    }

    private void OnKickedFromLobby()
    {
        // Once kicked, events will never trigger again, so we clear references.
        m_LobbyEvents = null;
        lobbyId = null;
        playersInLobby = 0;

        Database.UserGameSelection.gameType = Taitiko.DB.GameType.Public;
    }

    private void OnLobbyEventConnectionStateChanged(LobbyEventConnectionState state)
    {
        // Useful for diagnosing lobby event connection issues.
        switch (state)
        {
            case LobbyEventConnectionState.Unsubscribed:
                Debug.Log("[LOBBY] Unsubscribed");
                break;
            case LobbyEventConnectionState.Subscribing:
                Debug.Log("[LOBBY] Subscribing");
                break;
            case LobbyEventConnectionState.Subscribed:
                Debug.Log("[LOBBY] Subscribed");
                break;
            case LobbyEventConnectionState.Unsynced:
                Debug.Log("[LOBBY] Unsynced");
                break;
            case LobbyEventConnectionState.Error:
                Debug.Log("[LOBBY] Error");
                break;
        }
    }

    /// <summary>
    /// Builds the local player's lobby data.
    /// 
    /// Note:
    /// - Data keys ("Name", "Skin", etc.) are part of the project's lobby UI protocol.
    /// - Values are pulled from project-specific Database user data.
    /// </summary>
    private Unity.Services.Lobbies.Models.Player GetPlayer()
    {
        return new Unity.Services.Lobbies.Models.Player(
            AuthenticationService.Instance.PlayerId,
            null,
            new Dictionary<string, PlayerDataObject>
            {
                { "Name", new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, Database.UserData.user.name) },
                { "Skin", new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, Database.UserData.user.user_config.taitiko.unity_id) },
                { "Win", new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, "0") },
                { "Checkpoint", new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, "0") },
                { "Rabbit", new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, "0") },
                { "Dash", new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, "0") }
            }
        );
    }

    /// <summary>
    /// Heartbeat loop: keeps lobby alive while hosting.
    /// Unity Lobby requires periodic pings from the host.
    /// </summary>
    IEnumerator HeartbeatLobbyCoroutine(float waitTimeSeconds)
    {
        var delay = new WaitForSecondsRealtime(waitTimeSeconds);

        while (!string.IsNullOrEmpty(lobbyId))
        {
            LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
            Debug.Log("Pinging lobby id: " + lobbyId);
            yield return delay;
        }

        Debug.Log("[LOBBY] Deleting HeartBeat.");
    }

    /// <summary>
    /// Updates lobby metadata. In this project, it's used to set a default selected map.
    /// </summary>
    async Task<bool> UpdateLobby()
    {
        try
        {
            UpdateLobbyOptions options = new UpdateLobbyOptions
            {
                HostId = AuthenticationService.Instance.PlayerId,
                Data = new Dictionary<string, DataObject>()
                {
                    {
                        "SelectedMap", new DataObject(
                            visibility: DataObject.VisibilityOptions.Public,
                            value: "EXAMPLE_MAP_ID",
                            index: DataObject.IndexOptions.S1
                        )
                    }
                }
            };

            var lobby = await LobbyService.Instance.UpdateLobbyAsync(lobbyId, options);
        }
        catch (LobbyServiceException e)
        {
            Debug.Log(e);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Called by the host when the match server allocation is ready.
    /// This writes the connection payload into lobby data so clients can read it and start the match.
    /// </summary>
    public async Task<bool> UpdateLobbyToPlay(string ticketId)
    {
        try
        {
            UpdateLobbyOptions options = new UpdateLobbyOptions
            {
                Data = new Dictionary<string, DataObject>()
                {
                    {
                        "GameServer", new DataObject(
                            visibility: DataObject.VisibilityOptions.Member,
                            value: ticketId,
                            index: DataObject.IndexOptions.S2
                        )
                    }
                }
            };

            var lobby = await LobbyService.Instance.UpdateLobbyAsync(lobbyId, options);
            Debug.Log("[Lobby] Changes updated correctly!");
        }
        catch (LobbyServiceException e)
        {
            Debug.Log(e);
            PrivateLobbyLocalManager.singleton.notFoundPopUpText.text = e.Message;
            PrivateLobbyLocalManager.singleton.notFoundPopUp.Show();
        }

        return true;
    }

    /// <summary>
    /// Host action: starts a private match by creating a matchmaker ticket using the lobby player list.
    /// </summary>
    public void startPrivateMatch()
    {
        var userCustomData = new Dictionary<string, object> { };

        userCustomData.Add("lives", 0);

        // Anonymized map ID (original was project-specific).
        userCustomData.Add("game", "EXAMPLE_PRIVATE_MAP_ID");
        userCustomData.Add("players", lobby.Players.Count);
        userCustomData.Add("mode", "party");

        List<MModels.Player> Players = new List<MModels.Player>();

        foreach (Unity.Services.Lobbies.Models.Player p in lobby.Players)
        {
            Players.Add(new MModels.Player(p.Id, userCustomData));
        }

        // MatchmakerManager.singleton.createPrivateMatchmakerticket(Players);
        MatchmakerManager.singleton.createMatchmakerticket(GameMode.party, "EXAMPLE_PRIVATE_MAP_ID", Players.Count, 0, Players);
    }

    /// <summary>
    /// Client flow: joins a private lobby by code.
    /// Shows user-friendly error messages for common Lobby error codes.
    /// </summary>
    public async void joinMatchByCode(string code)
    {
        bool hasFailed = false;
        Lobby joinedLobby = null;

        try
        {
            JoinLobbyByCodeOptions options = new JoinLobbyByCodeOptions
            {
                Player = GetPlayer()
            };

            joinedLobby = await LobbyService.Instance.JoinLobbyByCodeAsync(code, options);
        }
        catch (LobbyServiceException e)
        {
            switch (e.ErrorCode)
            {
                case 16001:
                    Debug.Log("Lobby Not Found!");
                    PrivateLobbyLocalManager.singleton.notFoundPopUpText.text = "Lobby not found";
                    break;

                case 16010:
                    Debug.Log("Bad lobby code format");
                    PrivateLobbyLocalManager.singleton.notFoundPopUpText.text = "Bad lobby code format";
                    break;

                default:
                    Debug.Log("Unexpected error joining lobby, code: " + e.ErrorCode);
                    PrivateLobbyLocalManager.singleton.notFoundPopUpText.text = "Unexpected error";
                    break;
            }

            Debug.Log(e.Reason);
            PrivateLobbyLocalManager.singleton.notFoundPopUp.Show();
            hasFailed = true;
        }

        if (hasFailed) return;

        lobby = joinedLobby;
        lobbyId = lobby.Id;

        RefreshVisual();

        // Subscribe to lobby events after joining.
        var callbacks = new LobbyEventCallbacks();
        callbacks.LobbyChanged += OnLobbyChanged;
        callbacks.KickedFromLobby += OnKickedFromLobby;
        callbacks.LobbyEventConnectionStateChanged += OnLobbyEventConnectionStateChanged;
        callbacks.PlayerJoined += OnLobbyPlayerJoined;
        callbacks.PlayerLeft += OnLobbyPlayerLeft;

        try
        {
            m_LobbyEvents = await Lobbies.Instance.SubscribeToLobbyEventsAsync(lobby.Id, callbacks);
        }
        catch (LobbyServiceException ex)
        {
            switch (ex.Reason)
            {
                case LobbyExceptionReason.AlreadySubscribedToLobby:
                    Debug.LogWarning($"Already subscribed to lobby[{lobby.Id}]. No need to subscribe again. Exception: {ex.Message}");
                    break;

                case LobbyExceptionReason.SubscriptionToLobbyLostWhileBusy:
                    Debug.LogError($"Lobby event subscription lost while subscribing. Exception: {ex.Message}");
                    throw;

                case LobbyExceptionReason.LobbyEventServiceConnectionError:
                    Debug.LogError($"Failed to connect to lobby events. Exception: {ex.Message}");
                    throw;

                default:
                    throw;
            }
        }

        PrivateLobbyLocalManager.singleton.createView.Hide();
        PrivateLobbyLocalManager.singleton.inGameView.Show();
    }

    public void joinMatchButton()
    {
        if (!string.IsNullOrEmpty(PrivateLobbyLocalManager.singleton.inputCodeText.text))
        {
            Debug.Log(PrivateLobbyLocalManager.singleton.inputCodeText.text);
            joinMatchByCode(PrivateLobbyLocalManager.singleton.inputCodeText.text);
        }
        else
        {
            throw new Exception("Empty Text on Lobby!");
        }
    }

    /// <summary>
    /// Variant of join flow used in a special event.
    /// Includes an economy decrement once the lobby join succeeds.
    /// </summary>
    public async void joinMatchByCodeXMas(string code)
    {
        Lobby joinedLobby = null;

        try
        {
            JoinLobbyByCodeOptions options = new JoinLobbyByCodeOptions
            {
                Player = GetPlayer()
            };

            joinedLobby = await LobbyService.Instance.JoinLobbyByCodeAsync(code, options);
        }
        catch (LobbyServiceException e)
        {
            switch (e.ErrorCode)
            {
                case 16001:
                    Debug.Log("Lobby Not Found!");
                    PrivateLobbyLocalManager.singleton.notFoundPopUpText.text = "Lobby not found";
                    break;

                case 16010:
                    Debug.Log("Bad lobby code format");
                    PrivateLobbyLocalManager.singleton.notFoundPopUpText.text = "Bad lobby code format";
                    break;

                case 16004:
                    Debug.Log("Lobby Full");
                    PrivateLobbyLocalManager.singleton.notFoundPopUpText.text = "Lobby is already full";
                    break;

                default:
                    Debug.Log("Unexpected error joining lobby, code: " + e.ErrorCode);
                    PrivateLobbyLocalManager.singleton.notFoundPopUpText.text = "Unexpected error";
                    break;
            }

            Debug.Log(e.Reason);
            PrivateLobbyLocalManager.singleton.notFoundPopUp.Show();
        }

        lobby = joinedLobby;
        lobbyId = lobby.Id;

        RefreshVisual();

        var callbacks = new LobbyEventCallbacks();
        callbacks.LobbyChanged += OnLobbyChanged;
        callbacks.KickedFromLobby += OnKickedFromLobby;
        callbacks.LobbyEventConnectionStateChanged += OnLobbyEventConnectionStateChanged;
        callbacks.PlayerJoined += OnLobbyPlayerJoined;
        callbacks.PlayerLeft += OnLobbyPlayerLeft;

        try
        {
            m_LobbyEvents = await Lobbies.Instance.SubscribeToLobbyEventsAsync(lobby.Id, callbacks);
        }
        catch (LobbyServiceException ex)
        {
            switch (ex.Reason)
            {
                case LobbyExceptionReason.AlreadySubscribedToLobby:
                    Debug.LogWarning($"Already subscribed to lobby[{lobby.Id}]. No need to subscribe again. Exception: {ex.Message}");
                    break;

                case LobbyExceptionReason.SubscriptionToLobbyLostWhileBusy:
                    Debug.LogError($"Lobby event subscription lost while subscribing. Exception: {ex.Message}");
                    throw;

                case LobbyExceptionReason.LobbyEventServiceConnectionError:
                    Debug.LogError($"Failed to connect to lobby events. Exception: {ex.Message}");
                    throw;

                default:
                    throw;
            }
        }

        // Anonymized economy currency id for portfolio.
        await EconomyService.Instance.PlayerBalances.DecrementBalanceAsync("EXAMPLE_CURRENCY_ID", 1);

        MenuController.singleton.OpenSection(10);

        PrivateLobbyLocalManager.singleton.createView.Hide();
        PrivateLobbyLocalManager.singleton.inGameView.Show();
    }
}
