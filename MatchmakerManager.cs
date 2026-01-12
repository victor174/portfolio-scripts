// NOTE: "Database", "TaitikoUS", "PopUpManager" are project-specific dependencies.
// In this portfolio sample they are intentionally kept to show integration points.


using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Headers;

using UnityEngine;
using UnityEngine.SceneManagement;

using TMPro;
using Doozy.Engine.UI;
using DG.Tweening;

using Unity.Services.Authentication;
using Unity.Services.Matchmaker;
using Unity.Services.Qos;

using MModels = Unity.Services.Matchmaker.Models;
using StatusOptions = Unity.Services.Matchmaker.Models.MultiplayAssignment.StatusOptions;

// Project-specific namespace/types (kept as-is; in a portfolio repo these could be stubbed/mocked).
using Taitiko;

public enum GameMode
{
    free,     // Public games without money
    paid,     // Public games with money
    party,    // Private games
    training, // Playing solo against time
}

public enum Map
{
    AR,
    GR,
    BC,
}

/// <summary>
/// Matchmaking manager using Unity Matchmaker + QoS.
/// 
/// High-level flow:
/// 1) Show preload UI and build player/custom attributes.
/// 2) Run QoS query to provide best regions to matchmaking.
/// 3) Create a matchmaking ticket.
/// 4) Poll ticket status until a MultiplayAssignment is found/failed/timed out.
/// 5) On "Found", store match connection data and load the gameplay scene.
/// 
/// Notes for portfolio reviewers:
/// - Some dependencies are project-specific (Database, TaitikoUS, PopUpManager, Doozy UIView, etc.).
/// - String literals that could expose internal naming were anonymized (auth scheme, scene name, etc.).
/// </summary>
public class MatchmakerManager : MonoBehaviour
{
    public string lastTicketID;

    public static MatchmakerManager singleton;

    [Header("UI / Loading")]
    public UIView preloadScreen;
    public TextMeshProUGUI preloadScreenText;
    public GameObject cancelButton;
    public eyeAnimationLoop leftEye, RightEye;
    public DOTweenAnimation fader;

    [Header("Project Services DB (project-specific)")]
    public TaitikoUS servicesDB;

    [Header("Loading Messages")]
    public string[] waitMessages;

    public bool privateGame;

    // Keeps the current matchmaking ticket id so we can cancel it if needed.
    private string matchmaketTicketId;

    private void Start()
    {
        // Simple singleton pattern for scene-bound manager.
        if (singleton == null)
            singleton = this;
        else
            Destroy(this);
    }

    /// <summary>
    /// Creates a matchmaking ticket for public/party/training flows.
    /// This method:
    /// - Builds custom data (lives, map, mode, cosmetics, etc.)
    /// - Collects QoS results
    /// - Calls Unity Matchmaker to create a ticket
    /// - Polls ticket status until assignment is found or fails
    /// </summary>
    public async void createMatchmakerticket(GameMode gameMode, string map, int nPlayers, int nLives, List<MModels.Player> players = null)
    {
        preloadScreen.Show();
        preloadScreenText.text = waitMessages[UnityEngine.Random.Range(0, waitMessages.Length)];

        var attributes = new Dictionary<string, object>();
        var userCustomData = new Dictionary<string, object>();

        // Authentication player id (Unity Services)
        Database.UserData.user.unity_id = playerIDStatusTicket();
        Debug.Log("Player id: " + Database.UserData.user.unity_id);

        userCustomData.Add("lives", nLives);
        Debug.Log("Map selected = " + map);

        userCustomData.Add("game", map);
        userCustomData.Add("players", nPlayers);
        userCustomData.Add("mode", gameMode.ToString());

        // Project-specific user data
        userCustomData.Add("userName", Database.UserData.user.name);
        userCustomData.Add("skinId", Database.UserData.user.user_config.taitiko.unity_id);
        userCustomData.Add("unityId", Database.UserData.user.unity_id);

        if (Api.singleton.isUsingElixir)
            userCustomData.Add("elixirId", Database.Elixir.userData.elixirId);

        // NOTE: This HttpClient is initialized with auth headers. In this snippet it is not used further.
        // Kept as-is to preserve original code behavior.
        HttpClient client = new HttpClient();
        // Anonymized auth scheme name (original was project-specific).
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("EXAMPLE_AUTH_SCHEME", AuthenticationService.Instance.AccessToken);

        // Project-specific config: fleet id used for QoS query
        var fleetId = Database.UnityServices.fleetId;

        // Fetch QoS results for the fleet (best regions, latency, packet loss, etc.)
        var qosResult = await QosService.Instance.GetSortedMultiplayQosResultsAsync(new List<string> { fleetId });

        List<MModels.QosResult> qosResults = new List<MModels.QosResult>();
        for (int i = 0; i < qosResult.Count; i++)
        {
            qosResults.Add(new MModels.QosResult(
                qosResult[i].Region,
                qosResult[i].PacketLossPercent,
                qosResult[i].AverageLatencyMs
            ));
        }

        // If no players list was provided, create a default one containing just the local player.
        if (players == null)
        {
            var player = new List<MModels.Player>
            {
                new MModels.Player(Database.UserData.user.unity_id, userCustomData, qosResults)
            };
            players = player;
        }

        // Project-specific config: queue name/id for matchmaker
        string queueName = Database.UnityServices.queuesId;

        // Attributes affect matchmaking rules (ex: lives, mode, player count, map).
        attributes.Add("lives", nLives);
        attributes.Add("game", map);
        attributes.Add("players", nPlayers);
        attributes.Add("mode", gameMode.ToString());

        Debug.Log("lives: " + nLives + " game type: " + map + " players: " + nPlayers + " Mode: " + gameMode);

        var options = new CreateTicketOptions(queueName, attributes);

        // Create ticket
        var ticketResponse = await MatchmakerService.Instance.CreateTicketAsync(players, options);

        Debug.Log(ticketResponse.Id);
        matchmaketTicketId = ticketResponse.Id;

        // Poll ticket status until we get a final assignment
        var ticketStatustask = ticketStatus(ticketResponse.Id);
        await ticketStatustask;

        Debug.Log("IP:" + ticketStatustask.Result.Ip + " PORT:" + ticketStatustask.Result.Port + " Match Id:" + ticketStatustask.Result.MatchId);

        // Dev tool integration (project-specific)
        if (Taitiko.DevTools.DeveloperTools.singleton != null)
        {
            Taitiko.DevTools.DeveloperTools.singleton.serverIp =
                ticketStatustask.Result.Ip + ":" + ticketStatustask.Result.Port;
        }

        lastTicketID = ticketResponse.Id;

        // Party mode: update lobby with server info (project-specific)
        if (gameMode == GameMode.party)
        {
            await PrivateLobbyManager.singleton.UpdateLobbyToPlay(
                servicesDB.match.matchServer + "$" + servicesDB.match.matchPort + "$" + servicesDB.match.matchId
            );
        }
    }

    /// <summary>
    /// Creates a private matchmaking ticket for a pre-assembled list of players.
    /// </summary>
    public async void createPrivateMatchmakerticket(List<MModels.Player> jugadores)
    {
        preloadScreen.Show();
        preloadScreenText.text = waitMessages[UnityEngine.Random.Range(0, waitMessages.Length)];

        var attributes = new Dictionary<string, object>();
        var userCustomData = new Dictionary<string, object>();

        Database.UserData.user.unity_id = playerIDStatusTicket();
        Debug.Log("Player id: " + Database.UserData.user.unity_id);

        // "Private" ticket attributes (kept as-is)
        userCustomData.Add("lives", 0);
        userCustomData.Add("game", 0);
        userCustomData.Add("players", 0);
        userCustomData.Add("accessibility", "private");

        // NOTE: This HttpClient is initialized with auth headers. In this snippet it is not used further.
        // Kept as-is to preserve original code behavior.
        HttpClient client = new HttpClient();
        // Anonymized auth scheme name (original was project-specific).
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("EXAMPLE_AUTH_SCHEME", AuthenticationService.Instance.AccessToken);

        // Project-specific config
        var fleetId = Database.UnityServices.queuesId; // Kept as original (even if naming suggests it might be queue id)

        var qosResult = await QosService.Instance.GetSortedMultiplayQosResultsAsync(new List<string> { fleetId });

        List<MModels.QosResult> qosResults = new List<MModels.QosResult>();
        for (int i = 0; i < qosResult.Count; i++)
        {
            qosResults.Add(new MModels.QosResult(
                qosResult[i].Region,
                qosResult[i].PacketLossPercent,
                qosResult[i].AverageLatencyMs
            ));
        }

        string queueName = Database.UnityServices.queuesId;

        attributes.Add("lives", 0);
        attributes.Add("game", 0);
        attributes.Add("players", 0);
        attributes.Add("accessibility", "private");

        var options = new CreateTicketOptions(queueName, attributes);

        var ticketResponse = await MatchmakerService.Instance.CreateTicketAsync(jugadores, options);

        Debug.Log(ticketResponse.Id);
        matchmaketTicketId = ticketResponse.Id;

        var ticketStatustask = ticketStatus(ticketResponse.Id);
        await ticketStatustask;

        Debug.Log("IP:" + ticketStatustask.Result.Ip + " PORT:" + ticketStatustask.Result.Port + " Match Id:" + ticketStatustask.Result.MatchId);

        if (Taitiko.DevTools.DeveloperTools.singleton != null)
        {
            Taitiko.DevTools.DeveloperTools.singleton.serverIp =
                ticketStatustask.Result.Ip + ":" + ticketStatustask.Result.Port;
        }

        lastTicketID = ticketResponse.Id;

        // Update lobby with server info (project-specific)
        await PrivateLobbyManager.singleton.UpdateLobbyToPlay(
            servicesDB.match.matchServer + "$" + servicesDB.match.matchPort + "$" + servicesDB.match.matchId
        );
    }

    /// <summary>
    /// Public UI entrypoint to cancel the current matchmaking ticket.
    /// </summary>
    public async void cancelTicket()
    {
        var cancelTask = cancelTicketTask();
        await cancelTask;
    }

    /// <summary>
    /// Deletes the current ticket and hides the preload UI.
    /// </summary>
    public async Task cancelTicketTask()
    {
        await MatchmakerService.Instance.DeleteTicketAsync(matchmaketTicketId);
        preloadScreen.Hide();
        preloadScreenText.text = "";
    }

    /// <summary>
    /// Polls the ticket until the assignment reaches a terminal state:
    /// - Found: match server allocation is returned (ip/port/matchId)
    /// - Failed / Timeout: terminal errors
    /// </summary>
    public async Task<MModels.MultiplayAssignment> ticketStatus(string ticketId)
    {
        MModels.MultiplayAssignment assignment = null;
        bool gotAssignment = false;

        do
        {
            // Rate limit delay (avoid hammering the endpoint).
            await Task.Delay(TimeSpan.FromSeconds(1f));

            preloadScreenText.text = waitMessages[UnityEngine.Random.Range(0, waitMessages.Length)];

            // Poll ticket state
            var ticketStatus = await MatchmakerService.Instance.GetTicketAsync(ticketId);
            if (ticketStatus == null)
                continue;

            // Convert platform ticket data to MultiplayAssignment if present.
            if (ticketStatus.Type == typeof(MModels.MultiplayAssignment))
                assignment = ticketStatus.Value as MModels.MultiplayAssignment;

            switch (assignment.Status)
            {
                case StatusOptions.Found:
                    gotAssignment = true;

                    // Store match info into a project-specific service DB.
                    servicesDB.match = new TaitikoUS.lastMatch();
                    servicesDB.match.matchId = assignment.MatchId;
                    servicesDB.match.matchServer = assignment.Ip;

                    Debug.Log(servicesDB.match.matchServer + servicesDB.match.matchPort);

                    servicesDB.match.matchPort = assignment.Port;

                    preloadScreenText.text = "Match found!";
                    leftEye.stopAnimate();
                    RightEye.stopAnimate();
                    cancelButton.SetActive(false);

                    await Task.Delay(TimeSpan.FromSeconds(1.5f));
                    fader.DOPlayById("fadeate");
                    await Task.Delay(TimeSpan.FromSeconds(1.5f));

                    // Anonymized scene name (original project scene name removed for portfolio).
                    SceneManager.LoadScene("EXAMPLE_GAME_SCENE");
                    break;

                case StatusOptions.InProgress:
                    // Still searching...
                    break;

                case StatusOptions.Failed:
                    gotAssignment = true;

                    // Project-specific UI error path
                    PopUpManager.singleton.ShowPopUp("Generic", "Error finding match: " + assignment.Message);
                    preloadScreenText.text = "Error finding match: " + assignment.Message;

                    Debug.LogError("Failed to get ticket status. Error: " + assignment.Message);
                    break;

                case StatusOptions.Timeout:
                    gotAssignment = true;
                    preloadScreenText.text = "No matches found, please try it again.";
                    break;

                default:
                    throw new InvalidOperationException();
            }
        }
        while (!gotAssignment);

        return assignment;
    }

    /// <summary>
    /// Retrieves the authenticated Unity Services player ID.
    /// This method loops until the ID is available.
    /// </summary>
    public string playerIDStatusTicket()
    {
        string playerIDStatus = null;
        bool gotAssignment = false;

        do
        {
            playerIDStatus = AuthenticationService.Instance.PlayerId;

            if (playerIDStatus == null)
            {
                Debug.Log("player ID is null, retrying...");
                continue;
            }

            gotAssignment = true;
        }
        while (!gotAssignment);

        return playerIDStatus;
    }

    /// <summary>
    /// Debug helper to print the last created ticket.
    /// (Kept as-is; note it logs the task object, not the resolved assignment.)
    /// </summary>
    public void viewMyLastTicket()
    {
        var assignment = MatchmakerService.Instance.GetTicketAsync(lastTicketID);
        Debug.Log("my last tiket is: " + assignment);
    }
}
