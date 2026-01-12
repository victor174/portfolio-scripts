using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Taitiko
{
    /// <summary>
    /// Utility class used to load, track, and release dynamically loaded Network Prefabs (Addressables).
    ///
    /// Why it exists:
    /// - Some network prefabs are not included in the build-time NetworkConfig prefab list.
    /// - Clients may need to preload them (via Addressables) before they can connect / spawn objects safely.
    ///
    /// What it provides:
    /// - Loading one or multiple prefabs by Addressable GUID.
    /// - Tracking which clients have loaded which prefab hashes.
    /// - Generating connection/disconnection payloads so server/client can agree on prefab sets.
    ///
    /// Notes:
    /// - An artificial delay can be enabled via scripting define symbol ENABLE_ARTIFICIAL_DELAY (disabled by default).
    /// - HashOfDynamicPrefabGUIDs is computed deterministically (sorted GUID list) so it matches across clients.
    /// </summary>
    /// <remarks>
    /// Artificial delay to the loading of a network prefab is disabled by default. To enable it, make sure to add
    /// ENABLE_ARTIFICIAL_DELAY as a scripting define symbol to your project's Player settings.
    /// </remarks>
    public static class DynamicPrefabLoadingUtilities
    {
        // Used when there are no loaded dynamic prefabs yet.
        const int k_EmptyDynamicPrefabHash = -1;

        /// <summary>
        /// Deterministic hash representing the current set of loaded dynamic prefab GUIDs.
        /// Used for connection validation (client/server must agree on the same prefab set).
        /// </summary>
        public static int HashOfDynamicPrefabGUIDs { get; private set; } = k_EmptyDynamicPrefabHash;

        // Tracks loaded Addressables handles, so we can release them later.
        static Dictionary<AddressableGUID, AsyncOperationHandle<GameObject>> s_LoadedDynamicPrefabResourceHandles =
            new Dictionary<AddressableGUID, AsyncOperationHandle<GameObject>>(new AddressableGUIDEqualityComparer());

        // Cached list to avoid GC allocations when computing hashes/payloads.
        static List<AddressableGUID> s_DynamicPrefabGUIDs = new List<AddressableGUID>();

        // Maps prefab GUID-hash -> set of clientIds who have confirmed loading it.
        static Dictionary<int, HashSet<ulong>> s_PrefabHashToClientIds = new Dictionary<int, HashSet<ulong>>();

        /// <summary>
        /// Returns true if the given clientId has already loaded (and reported) the prefab hash.
        /// </summary>
        public static bool HasClientLoadedPrefab(ulong clientId, int prefabHash) =>
            s_PrefabHashToClientIds.TryGetValue(prefabHash, out var clientIds) && clientIds.Contains(clientId);

        /// <summary>
        /// Returns true if the prefab GUID is loaded locally (handle exists in the loaded dictionary).
        /// </summary>
        public static bool IsPrefabLoadedOnAllClients(AddressableGUID assetGuid) =>
            s_LoadedDynamicPrefabResourceHandles.ContainsKey(assetGuid);

        /// <summary>
        /// Try-get loaded operation handle for a given prefab GUID.
        /// </summary>
        public static bool TryGetLoadedGameObjectFromGuid(AddressableGUID assetGuid, out AsyncOperationHandle<GameObject> loadedGameObject)
        {
            return s_LoadedDynamicPrefabResourceHandles.TryGetValue(assetGuid, out loadedGameObject);
        }

        /// <summary>
        /// Exposes loaded resource handles for external systems (debugging/inspection).
        /// </summary>
        public static Dictionary<AddressableGUID, AsyncOperationHandle<GameObject>> LoadedDynamicPrefabResourceHandles =>
            s_LoadedDynamicPrefabResourceHandles;

        public static int LoadedPrefabCount => s_LoadedDynamicPrefabResourceHandles.Count;

        // NetworkManager reference is required to Add/Remove prefabs at runtime.
        static NetworkManager s_NetworkManager;

        static DynamicPrefabLoadingUtilities() { }

        /// <summary>
        /// Initializes the utilities with a NetworkManager instance.
        /// Must be called before loading/unloading prefabs.
        /// </summary>
        public static void Init(NetworkManager networkManager)
        {
            s_NetworkManager = networkManager;
        }

        /// <remarks>
        /// This is not the most optimal algorithm for big quantities of Addressables, but easy enough to maintain for a
        /// small number like in this sample. One could use a "client dirty" algorithm to mark clients needing loading
        /// or not instead, but that would require more complex dirty management.
        /// </remarks>
        public static void RecordThatClientHasLoadedAllPrefabs(ulong clientId)
        {
            foreach (var dynamicPrefabGUID in s_DynamicPrefabGUIDs)
            {
                RecordThatClientHasLoadedAPrefab(dynamicPrefabGUID.GetHashCode(), clientId);
            }
        }

        /// <summary>
        /// Records that a client has loaded a single prefab identified by its GUID hash.
        /// </summary>
        public static void RecordThatClientHasLoadedAPrefab(int assetGuidHash, ulong clientId)
        {
            if (s_PrefabHashToClientIds.TryGetValue(assetGuidHash, out var clientIds))
            {
                clientIds.Add(clientId);
            }
            else
            {
                s_PrefabHashToClientIds.Add(assetGuidHash, new HashSet<ulong>() { clientId });
            }
        }

        /// <summary>
        /// Generates a lightweight connection payload sent by the client.
        /// Currently contains only the hash of the dynamic prefab GUIDs set.
        /// </summary>
        public static byte[] GenerateRequestPayload()
        {
            var payload = JsonUtility.ToJson(new ConnectionPayload()
            {
                hashOfDynamicPrefabGUIDs = HashOfDynamicPrefabGUIDs
            });

            return System.Text.Encoding.UTF8.GetBytes(payload);
        }

        /// <remarks>
        /// Testing showed that with the current implementation, Netcode for GameObjects will send the DisconnectReason
        /// message as a non-fragmented message, meaning that the upper limit of this message in bytes is exactly
        /// NON_FRAGMENTED_MESSAGE_MAX_SIZE bytes (1300 at the time of writing), defined inside of
        /// <see cref="MessagingSystem"/>.
        /// For this reason, DisconnectReason should only be used to instruct the user "why" a connection failed, and
        /// "where" to fetch the relevant connection data. We recommend using services like UGS to fetch larger batches
        /// of data.
        /// </remarks>
        public static string GenerateDisconnectionPayload()
        {
            // If the client is missing required prefabs, we can tell them which GUIDs they must preload.
            var rejectionPayload = new DisconnectionPayload()
            {
                reason = DisconnectReason.ClientNeedsToPreload,
                guids = s_DynamicPrefabGUIDs.Select(item => item.ToString()).ToList()
            };

            return JsonUtility.ToJson(rejectionPayload);
        }

        /// <summary>
        /// Loads a single dynamic prefab by Addressable GUID, adds it as a Network Prefab,
        /// tracks the Addressables handle, and optionally recomputes the GUID set hash.
        /// </summary>
        public static async Task<GameObject> LoadDynamicPrefab(
            AddressableGUID guid,
            int artificialDelayMilliseconds,
            bool recomputeHash = true)
        {
            // If already loaded, reuse the loaded prefab.
            if (s_LoadedDynamicPrefabResourceHandles.ContainsKey(guid))
            {
                Debug.Log($"Prefab has already been loaded, skipping loading this time | {guid}");
                return s_LoadedDynamicPrefabResourceHandles[guid].Result;
            }

            Debug.Log($"Loading dynamic prefab {guid.Value}");
            var op = Addressables.LoadAssetAsync<GameObject>(guid.ToString());
            var prefab = await op.Task;

#if ENABLE_ARTIFICIAL_DELAY
            // Educational/testing-only: simulate slow loads to validate preload UX.
            await Task.Delay(artificialDelayMilliseconds);
#endif

            // Register prefab with Netcode so it can be spawned over the network.
            s_NetworkManager.AddNetworkPrefab(prefab);

            // Store handle so we can release it later.
            s_LoadedDynamicPrefabResourceHandles.Add(guid, op);

            if (recomputeHash)
            {
                CalculateDynamicPrefabArrayHash();
            }

            return prefab;
        }

        /// <summary>
        /// Loads multiple prefabs in parallel and recomputes the GUID set hash once at the end.
        /// </summary>
        public static async Task<IList<GameObject>> LoadDynamicPrefabs(AddressableGUID[] guids, int artificialDelaySeconds = 0)
        {
            var tasks = new List<Task<GameObject>>();

            foreach (var guid in guids)
            {
                tasks.Add(LoadDynamicPrefab(guid, artificialDelaySeconds, recomputeHash: false));
            }

            var prefabs = await Task.WhenAll(tasks);
            CalculateDynamicPrefabArrayHash();

            return prefabs;
        }

        /// <summary>
        /// Refreshes the cached GUID list from the currently loaded handle dictionary.
        /// </summary>
        public static void RefreshLoadedPrefabGuids()
        {
            s_DynamicPrefabGUIDs.Clear();
            s_DynamicPrefabGUIDs.AddRange(s_LoadedDynamicPrefabResourceHandles.Keys);
        }

        /// <summary>
        /// Computes a deterministic hash for the currently loaded prefab GUID list.
        /// - Sorts GUIDs to ensure consistent order across clients.
        /// - Combines hash codes using a simple stable algorithm.
        /// </summary>
        static void CalculateDynamicPrefabArrayHash()
        {
            // We sort so the hash is consistent across different clients/processes.
            RefreshLoadedPrefabGuids();
            s_DynamicPrefabGUIDs.Sort((a, b) => a.Value.CompareTo(b.Value));

            HashOfDynamicPrefabGUIDs = k_EmptyDynamicPrefabHash;

            // Hash combination algorithm suggested by Jon Skeet:
            // https://stackoverflow.com/questions/1646807/quick-and-simple-hash-code-combinations
            // We avoid System.HashCode.Combine because it can vary across processes by design.
            unchecked
            {
                int hash = 17;
                for (var i = 0; i < s_DynamicPrefabGUIDs.Count; ++i)
                {
                    hash = hash * 31 + s_DynamicPrefabGUIDs[i].GetHashCode();
                }

                HashOfDynamicPrefabGUIDs = hash;
            }

            Debug.Log($"Calculated hash of dynamic prefabs: {HashOfDynamicPrefabGUIDs}");
        }

        /// <summary>
        /// Unregisters all loaded dynamic prefabs from Netcode and releases Addressables handles.
        /// </summary>
        public static void UnloadAndReleaseAllDynamicPrefabs()
        {
            HashOfDynamicPrefabGUIDs = k_EmptyDynamicPrefabHash;

            foreach (var handle in s_LoadedDynamicPrefabResourceHandles.Values)
            {
                s_NetworkManager.RemoveNetworkPrefab(handle.Result);
                Addressables.Release(handle);
            }

            s_LoadedDynamicPrefabResourceHandles.Clear();
        }
    }
}
