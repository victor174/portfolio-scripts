using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AddressableAssets;
using Random = UnityEngine.Random;

namespace Taitiko.ServerDynamicLoader
{
    /// <summary>
    /// (EN) Playground / sample-style class showcasing dynamic loading of Network Prefabs (Addressables) at runtime.
    ///
    /// Key use-cases covered:
    /// - Connection Approval: validate that late-join clients have the same dynamic prefab set as the server.
    /// - Preloading: load a list of network prefabs on host and instruct clients to do the same via ClientRpc.
    /// - Spawn & Visibility: spawn network objects even if some clients haven't loaded the prefab yet, keeping them hidden
    ///   until the client acknowledges the prefab load.
    ///
    /// (ES) Esta clase sirve como el patio de recreo de los casos de uso de carga dinamica de prefabs. Integra API de esta muestra
    /// para usar en el tiempo posterior a la conexion, como: aprobacion de conexion para sincronizar clientes que se unen tarde, 
    /// carga dinamica de una coleccion de prefabs de red en el host y todos los clientes conectados, generacion sincrona de un 
    /// prefab de red cargado dinamicamente en todos los clientes conectados y generacion de un prefab de red cargado 
    /// dinamicamente como invisible para todos los clientes hasta que carguen el prefab localmente (en cuyo caso se vuelve visible 
    /// en red para el cliente).
    /// </summary>
    /// <remarks>
    /// (EN) For more details on the intended usage, refer to the project's readme / technical doc (project-specific).
    /// 
    /// (ES) Para obtener mas detalles sobre el uso de la API, consulte el archivo readme del proyecto (que incluye enlaces a 
    /// recursos adicionales, incluido el documento tecnico del proyecto).
    /// </remarks>
    public class ServerDynamicLoader : NetworkBehaviour
    {
        [SerializeField] private NetworkManager m_NetworkManager;
        [SerializeField] private List<AssetReferenceGameObject> m_DynamicPrefabReferences;

        // (EN) Sample limits / guards
        private const int k_MaxConnectedClientCount = 4;
        private const int k_MaxConnectPayload = 1024;

        // (ES) Un almacenamiento donde guardamos la asociacion entre el prefab (hash de su GUID)
        // y los objetos de red generados que lo utilizan
        private readonly Dictionary<int, HashSet<NetworkObject>> m_PrefabHashToNetworkObjectId =
            new Dictionary<int, HashSet<NetworkObject>>();

        private void Start()
        {
            // (EN) Initialize shared dynamic prefab utilities with the NetworkManager.
            // (ES) seteamos la variable m_NetworkManager en s_NetworkManager en el script DynamicPrefabLoadingUtilities
            DynamicPrefabLoadingUtilities.Init(m_NetworkManager);

            // (EN) Enable Connection Approval so the server can accept/deny based on prefab sync, capacity, etc.
            // (ES) En los casos de uso donde se implementa la aprobacion de conexion...
            m_NetworkManager.NetworkConfig.ConnectionApproval = true;

            // (EN) ForceSamePrefabs must be disabled to allow runtime prefab registration.
            // (ES) Aqui, mantenemos ForceSamePrefabs desactivado...
            m_NetworkManager.NetworkConfig.ForceSamePrefabs = false;

            // (EN) Hook the approval callback.
            // (ES) Comprobamos si nos podemos conectar...
            m_NetworkManager.ConnectionApprovalCallback += ConnectionApprovalCallback;
        }

        public override void OnDestroy()
        {
            m_NetworkManager.ConnectionApprovalCallback -= ConnectionApprovalCallback;

            // (EN) Release loaded Addressables + unregister network prefabs.
            DynamicPrefabLoadingUtilities.UnloadAndReleaseAllDynamicPrefabs();

            base.OnDestroy();
        }

        private void ConnectionApprovalCallback(
            NetworkManager.ConnectionApprovalRequest request,
            NetworkManager.ConnectionApprovalResponse response)
        {
            Debug.Log("El cliente esta intentando conectarse " + request.ClientNetworkId);

            var connectionData = request.Payload;
            var clientId = request.ClientNetworkId;

            if (clientId == m_NetworkManager.LocalClientId)
            {
                // (EN) Allow host to connect immediately.
                // (ES) permitir que el host se conecte
                Approve();
                return;
            }

            // (ES) Denegacion especifica de la muestra en clientes despues de que se hayan conectado k_MaxConnectedClientCount clientes
            if (m_NetworkManager.ConnectedClientsList.Count >= k_MaxConnectedClientCount)
            {
                ImmediateDeny();
                return;
            }

            if (connectionData.Length > k_MaxConnectPayload)
            {
                // (EN) Lightweight protection: if payload is too large, deny immediately (avoid wasting server time).
                // (ES) Si connectionData es demasiado grande, denegar de inmediato...
                ImmediateDeny();
                return;
            }

            if (DynamicPrefabLoadingUtilities.LoadedPrefabCount == 0)
            {
                // (EN) If no dynamic prefabs were loaded on the server, there is nothing to validate yet.
                // (ES) aprobar de inmediato la conexion si aun no hemos cargado ningun prefab
                Approve();
                return;
            }

            var payload = System.Text.Encoding.UTF8.GetString(connectionData);
            // https://docs.unity3d.com/2020.2/Documentation/Manual/JSONSerialization.html
            var connectionPayload = JsonUtility.FromJson<ConnectionPayload>(payload);

            int clientPrefabHash = connectionPayload.hashOfDynamicPrefabGUIDs;
            int serverPrefabHash = DynamicPrefabLoadingUtilities.HashOfDynamicPrefabGUIDs;

            // (ES) si el cliente tiene los mismos prefabs que el servidor, aprobar la conexion
            if (clientPrefabHash == serverPrefabHash)
            {
                Approve();

                // (EN) Mark client as having all required prefabs loaded.
                DynamicPrefabLoadingUtilities.RecordThatClientHasLoadedAllPrefabs(clientId);
                return;
            }

            // (EN) If prefab sets mismatch, deny and provide a disconnection reason/payload.
            // (ES) Para que los clientes no se desconecten sin recibir retroalimentacion...
            DynamicPrefabLoadingUtilities.RefreshLoadedPrefabGuids();
            response.Reason = DynamicPrefabLoadingUtilities.GenerateDisconnectionPayload();

            ImmediateDeny();

            void Approve()
            {
                Debug.Log($"Cliente {clientId} aprobado");
                response.Approved = true;

                // (EN) This sample does not spawn a default player object on connect.
                response.CreatePlayerObject = false;
            }

            void ImmediateDeny()
            {
                Debug.Log($"Cliente {clientId} denegado de la conexion");
                response.Approved = false;
                response.CreatePlayerObject = false;
            }
        }

        public void OnConnectNewPlayer()
        {
            // Intentionally left empty in the sample.
        }

        public void OnCallPreload()
        {
            if (!m_NetworkManager.IsServer)
                return;

            PreloadPrefabs();
        }

        public void OnCallTrySpawnInvisible()
        {
            if (!m_NetworkManager.IsServer)
                return;

            TrySpawnInvisible();
        }

        private async void PreloadPrefabs()
        {
            var tasks = new List<Task>();

            foreach (var p in m_DynamicPrefabReferences)
            {
                tasks.Add(PreloadDynamicPrefabOnServerAndStartLoadingOnAllClients(p.AssetGUID));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// (EN) Preloads a dynamic prefab on the server and broadcasts a ClientRpc so all clients start loading it too.
        /// (ES) Esta funcion precarga el prefab dinamico en el servidor y envia un RPC de cliente a todos los clientes para hacer lo mismo.
        /// </summary>
        private async Task PreloadDynamicPrefabOnServerAndStartLoadingOnAllClients(string guid)
        {
            if (!m_NetworkManager.IsServer)
                return;

            var assetGuid = new AddressableGUID { Value = guid };

            if (DynamicPrefabLoadingUtilities.IsPrefabLoadedOnAllClients(assetGuid))
            {
                Debug.Log("El prefab ya esta cargado por todos los pares");
                return;
            }

            Debug.Log("Cargando prefab dinamico en los clientes...");
            LoadAddressableClientRpc(assetGuid);

            await DynamicPrefabLoadingUtilities.LoadDynamicPrefab(assetGuid, 1000);

            // (EN) Server loaded the prefab; sample could update UI here if needed.
            DynamicPrefabLoadingUtilities.TryGetLoadedGameObjectFromGuid(assetGuid, out var loadedGameObject);
        }

        [ClientRpc]
        private void LoadAddressableClientRpc(AddressableGUID guid, ClientRpcParams rpcParams = default)
        {
            // (EN) Host already loads server-side; only pure clients should run the client load.
            if (!IsHost)
                Load(guid);

            async void Load(AddressableGUID assetGuid)
            {
                Debug.Log("Cargando prefab dinamico en el cliente...");
                await DynamicPrefabLoadingUtilities.LoadDynamicPrefab(assetGuid, 1000);
                Debug.Log("El cliente cargo el prefab dinamico");

                DynamicPrefabLoadingUtilities.TryGetLoadedGameObjectFromGuid(assetGuid, out var loadedGameObject);

                // (EN) Notify server that this client can now safely render/spawn objects using this prefab.
                AcknowledgeSuccessfulPrefabLoadServerRpc(assetGuid.GetHashCode());
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void AcknowledgeSuccessfulPrefabLoadServerRpc(int prefabHash, ServerRpcParams rpcParams = default)
        {
            // m_SynchronousSpawnAckCount++;
            Debug.Log($"El cliente reconocio la carga exitosa del prefab con hash: {prefabHash}");

            DynamicPrefabLoadingUtilities.RecordThatClientHasLoadedAPrefab(
                prefabHash,
                rpcParams.Receive.SenderClientId
            );

            // (EN) Server already sees everything; only update visibility for non-host clients.
            if (rpcParams.Receive.SenderClientId != m_NetworkManager.LocalClientId)
            {
                // (EN) Security note (as in original): if visibility is gameplay-relevant, avoid trusting client RPCs blindly.
                ShowHiddenObjectsToClient(prefabHash, rpcParams.Receive.SenderClientId);
            }

            // (ES) una forma rapida de obtener el nombre de una referencia de prefab coincidente a traves de su hash de prefab
            var loadedPrefabName = "Indefinido";

            foreach (var prefabReference in m_DynamicPrefabReferences)
            {
                var prefabReferenceGuid = new AddressableGUID { Value = prefabReference.AssetGUID };

                if (prefabReferenceGuid.GetHashCode() == prefabHash)
                {
                    if (DynamicPrefabLoadingUtilities.LoadedDynamicPrefabResourceHandles.TryGetValue(
                            prefabReferenceGuid,
                            out var loadedGameObject))
                    {
                        loadedPrefabName = loadedGameObject.Result.name;
                    }
                    break;
                }
            }
        }

        private async void TrySpawnInvisible()
        {
            var randomPrefab = m_DynamicPrefabReferences[Random.Range(0, m_DynamicPrefabReferences.Count)];
            await SpawnImmediatelyAndHideUntilPrefabIsLoadedOnClient(
                randomPrefab.AssetGUID,
                Random.insideUnitCircle * 5,
                Quaternion.identity
            );
        }

        /// <summary>
        /// (EN) Spawns a network object immediately, even if some clients haven't loaded the prefab yet.
        /// Those clients won't see the object until they load + acknowledge the prefab (then server shows it).
        /// 
        /// (ES) Este metodo spawnear un prefab addressable por su GUID. No asegura que todos los clientes hayan cargado el
        /// prefab antes de spawnearlo. Todos los objetos spawnados son invisibles a los clientes que no tienen el prefab
        /// cargado. El servidor le dice a los clientes que no tienen el prefab precargado que lo carguen y lo reconozcan,
        /// y luego el servidor hace que el objeto sea visible para ese cliente.
        /// </summary>
        private async Task<NetworkObject> SpawnImmediatelyAndHideUntilPrefabIsLoadedOnClient(
            string guid,
            Vector3 position,
            Quaternion rotation)
        {
            if (IsServer)
            {
                var assetGuid = new AddressableGUID { Value = guid };
                return await Spawn(assetGuid);
            }

            return null;

            async Task<NetworkObject> Spawn(AddressableGUID assetGuid)
            {
                var prefab = await DynamicPrefabLoadingUtilities.LoadDynamicPrefab(assetGuid, 1000);

                DynamicPrefabLoadingUtilities.TryGetLoadedGameObjectFromGuid(assetGuid, out var loadedGameObject);

                var obj = Instantiate(prefab, position, rotation).GetComponent<NetworkObject>();

                if (m_PrefabHashToNetworkObjectId.TryGetValue(assetGuid.GetHashCode(), out var networkObjectIds))
                {
                    networkObjectIds.Add(obj);
                }
                else
                {
                    m_PrefabHashToNetworkObjectId.Add(
                        assetGuid.GetHashCode(),
                        new HashSet<NetworkObject> { obj }
                    );
                }

                // (EN) Custom visibility check:
                // - Server always sees it
                // - Clients see it only after they confirm the prefab is loaded
                // - Otherwise we trigger a targeted preload and keep it hidden
                obj.CheckObjectVisibility = (clientId) =>
                {
                    if (clientId == NetworkManager.ServerClientId)
                        return true;

                    if (DynamicPrefabLoadingUtilities.HasClientLoadedPrefab(clientId, assetGuid.GetHashCode()))
                        return true;

                    LoadAddressableClientRpc(
                        assetGuid,
                        new ClientRpcParams
                        {
                            Send = new ClientRpcSendParams
                            {
                                TargetClientIds = new ulong[] { clientId }
                            }
                        }
                    );

                    return false;
                };

                obj.Spawn();
                return obj;
            }
        }

        private void ShowHiddenObjectsToClient(int prefabHash, ulong clientId)
        {
            if (m_PrefabHashToNetworkObjectId.TryGetValue(prefabHash, out var networkObjects))
            {
                foreach (var obj in networkObjects)
                {
                    if (!obj.IsNetworkVisibleTo(clientId))
                    {
                        obj.NetworkShow(clientId);
                    }
                }
            }
        }
    }
}
