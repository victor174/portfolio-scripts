using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UnityEngine.ResourceManagement.AsyncOperations;

using static Responses;

public class GoldenArenaRankingController : MonoBehaviour
{
    public static GoldenArenaRankingController singleton;

    [Header("UI References")]
    public Transform rankContainer;
    public GoldenArenaRankingItem myRank;
    public TMP_Dropdown rankingDropdown;

    private string rankingRaw;

    // Portfolio-safe constants (sanitized from production values).
    private const string LocalizationTableName = "Common";
    private const string PoolKeyRankItem = "GoldenArenaRankItem";
    private const string ApiRankingEndpoint = "/api/rankings"; // sanitized endpoint
    private const string ApiParamGameIdKey = "game_id";        // sanitized param key
    private const string PopupTitle = "Oops...";
    private const string PopupButton = "OK";

    private const int SectionIdGeneralRanking = 12;
    private const int SectionIdTournamentRanking = 18;

    /*
     * Leaderboard UI controller.
     * - Fetches and renders leaderboard entries for different modes (game-specific, global, tournament).
     * - Uses pooling to efficiently rebuild UI lists.
     * - Uses a dropdown to switch the displayed ranking mode (gameId mapping).
     */
    private void Awake()
    {
        if (singleton == null)
        {
            singleton = this;
            return;
        }

        Destroy(this);
    }

    private void Start()
    {
        // Registers navigation callbacks so this screen can be opened/rebuilt by the app navigation system.

        // General Ranking section (Section 12) => initialize by fetching the default ranking.
        var rankCallback = new NavigationCallback
        {
            sectionId = SectionIdGeneralRanking,
            callbackFunction = GetInitRank
        };
        AppManager.singleton.callBacks.Add(rankCallback);

        // Tournament Ranking section (Section 18)
        // Note: Tournament rankings are mounted manually from another flow (e.g., a tournament feed item).
        // We register an empty callback so AppManager can recognize the section.
        var tournamentRankCallback = new NavigationCallback
        {
            sectionId = SectionIdTournamentRanking,
            callbackFunction = null
        };
        AppManager.singleton.callBacks.Add(tournamentRankCallback);
    }

    public async void SetOptionsRanking()
    {
        // Builds dropdown labels from the localization table (portfolio-safe localization keys).
        var keys = new List<string> { "select-bomb", "select-jethero", "select-zurvive" };

        rankingDropdown.ClearOptions();
        var translatedOptions = new List<string>();

        AsyncOperationHandle<StringTable> tableHandle =
            LocalizationSettings.StringDatabase.GetTableAsync(LocalizationTableName);

        await tableHandle.Task;

        if (tableHandle.Status != AsyncOperationStatus.Succeeded)
        {
            Debug.LogWarning($"Localization table '{LocalizationTableName}' could not be loaded.");
            return;
        }

        StringTable localizedTable = tableHandle.Result;

        foreach (string key in keys)
        {
            var entry = localizedTable.GetEntry(key);
            if (entry == null)
            {
                Debug.LogWarning($"Localization key '{key}' not found in table '{LocalizationTableName}'.");
                continue;
            }

            translatedOptions.Add(entry.GetLocalizedString());
        }

        rankingDropdown.AddOptions(translatedOptions);
    }

    public void GetInitRank()
    {
        // Default initial ranking.
        GetRank(1);
    }

    public void GetRank(int gameId)
    {
        // Fetch leaderboard data from backend and rebuild the UI list.
        // Pool is cleared before mounting to avoid stale entries and keep UI consistent.

        var parameters = new List<Api.Parameter>
        {
            new Api.Parameter(ApiParamGameIdKey, gameId.ToString())
        };

        rankingDropdown.gameObject.SetActive(true);
        PoolingManager.singleton.CleanPool(PoolKeyRankItem);

        StartCoroutine(Api.singleton.Call(ApiRankingEndpoint, Api.Method.Post, parameters, true, response =>
        {
            if (response.statusCode == 200)
            {
                rankingRaw = response.content;
                MountRank(rankingRaw, gameId);
                return;
            }

            SimpleError content = JsonUtility.FromJson<SimpleError>(response.content);
            PopUpController.Singleton.showPopUpOne(
                PopupTitle,
                content.message,
                PopupButton,
                PopUpController.Singleton.Dismiss,
                true
            );
        }));
    }

    public void MountRank(string raw, int gameId)
    {
        // Renders either:
        // - a game-specific leaderboard (gameId != 0), or
        // - a global leaderboard (gameId == 0).
        // Also highlights the current player ("myRank") when the backend includes the "you" entry.

        rankingDropdown.gameObject.SetActive(true);

        if (gameId != 0)
        {
            GoldenArenaRankResponse content = JsonUtility.FromJson<GoldenArenaRankResponse>(raw);

            for (int i = 0; i < content.scores.Length; i++)
            {
                GoldenArenaRankingItem item = PoolingManager.singleton
                    .GetObjectFromPool(rankContainer, 0, PoolKeyRankItem)
                    .GetComponent<GoldenArenaRankingItem>();

                item.me = content.scores[i];
                item.Display(i + 1);
            }

            // Show current user row if backend returned a valid "you" object.
            if (content.you.score.battle_arena_game_id != 0)
            {
                myRank.gameObject.SetActive(true);
                myRank.me.score = content.you.score.score;
                myRank.me.user.username = UserManager.singleton.user.username;
                myRank.me.user.character = UserManager.singleton.user.character;
                myRank.Display(content.you.position + 1);
            }
            else
            {
                myRank.gameObject.SetActive(false);
            }

            return;
        }

        GoldenArenaGlobalRankResponse globalContent = JsonUtility.FromJson<GoldenArenaGlobalRankResponse>(raw);

        myRank.gameObject.SetActive(false);

        for (int i = 0; i < globalContent.scores.Length; i++)
        {
            GoldenArenaRankingItem item = PoolingManager.singleton
                .GetObjectFromPool(rankContainer, 0, PoolKeyRankItem)
                .GetComponent<GoldenArenaRankingItem>();

            item.meGlobal = globalContent.scores[i];
            item.DisplayGlobal(i + 1);
        }

        // Show current user row if backend returned it.
        if (globalContent.you.score != null)
        {
            myRank.gameObject.SetActive(true);
            myRank.me.score = globalContent.you.score.score;
            myRank.Display(globalContent.you.position + 1);
        }
        else
        {
            myRank.gameObject.SetActive(false);
        }
    }

    public void MountTournamentRank(Responses.GoldenArenaTournamentFeedItem tournament)
    {
        // Tournament rankings come from a feed item already loaded elsewhere.
        // This method only renders the list and highlights the current user if found.

        PoolingManager.singleton.CleanPool(PoolKeyRankItem);
        rankingDropdown.gameObject.SetActive(false);

        for (int i = 0; i < tournament.tickets.Length; i++)
        {
            GoldenArenaRankingItem item = PoolingManager.singleton
                .GetObjectFromPool(rankContainer, 0, PoolKeyRankItem)
                .GetComponent<GoldenArenaRankingItem>();

            item.meTournament = tournament.tickets[i];
            item.DisplayTournament(i + 1);

            if (tournament.tickets[i].battle_user_id != UserManager.singleton.user.id)
                continue;

            try
            {
                Responses.TournamentTicket ticket = tournament.tickets
                    .Where(x => x.battle_user_id == UserManager.singleton.user.id)
                    .First();

                myRank.gameObject.SetActive(true);
                myRank.me.score = ticket.max_score;
                myRank.me.user.username = UserManager.singleton.user.username;
                myRank.me.user.character = UserManager.singleton.user.character;
                myRank.me.hasPlayed = ticket.hasPlayed;
                myRank.DisplayMeTournament(i + 1);
            }
            catch
            {
                myRank.gameObject.SetActive(false);
            }
        }
    }

    public void OnDropdownValueChange()
    {
        // Dropdown index maps directly to gameId (offset by +1).
        GetRank(rankingDropdown.value + 1);
    }
}