using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Single-player wave system that spawns enemies in timed steps, waits for clears, supports checkpoints,
/// and includes optional deterministic spawning (seeded RNG).
///
/// Portfolio notes:
/// - Integrates with pooling, enemy reset hooks, and progression systems (project-specific dependencies).
/// - Includes a "telegraph + spawn" flow and robust spawn validation using ground raycasts + capsule overlap checks.
/// - Contains editor-only debug tools (gizmos + context menus) to visualize and diagnose spawn failures.
/// </summary>
public class SinglePlayerWaveManager : MonoBehaviour
{
    public static SinglePlayerWaveManager singleton;

    [Header("Jugador / Zona de Spawn")]
    [Tooltip("Referencia al jugador para calcular el anillo de spawn.")]
    public Transform player;
    [Min(0f)] public float minRadius = 6f;
    [Min(0f)] public float maxRadius = 18f;

    [Tooltip("Altura a la que intentamos colocar el spawn antes de raycast al suelo.")]
    public float spawnCastHeight = 20f;

    [Tooltip("Capa del suelo para hacer raycast y colocar bien la Y.")]
    public LayerMask groundMask;

    [Tooltip("Capas con las que NO queremos colisionar en el punto de spawn (paredes, props, etc).")]
    public LayerMask blockedBy;

    [Tooltip("Radius check to prevent spawning inside geometry.")]
    public float spawnClearRadius = 0.5f;

    [Tooltip("Maximum attempts to find a valid spawn position per instance.")]
    public int maxPlacementTries = 12;

    [Header("Parent & Hierarchy Order")]
    public Transform pooledParent;
    public int pooledSiblingIndex = 0;

    [Header("Enemy Catalog (SinglePlayerEnemyAsset ScriptableObjects)")]
    public List<SinglePlayerEnemyAsset> enemyCatalog = new List<SinglePlayerEnemyAsset>();

    // (EN) Quick lookup: poolName -> known pools (optional).
    private HashSet<string> _knownPools = new HashSet<string>();

    [Header("Wave Definitions")]
    public List<Wave> waves = new List<Wave>();

    [Header("Eventos globales")]
    public UnityEvent onAllWavesCompleted;

    [Header("Determinismo")]
    [Tooltip("-1 = random per session. >= 0 = fixed RNG seed for deterministic spawn positions.")]
    public int rngSeed = -1;

    [Header("Sistema de Conejo Coleccionable")]
    [Tooltip("Referencia directa al GameObject del conejo en la escena")]
    public GameObject collectableRabbit;

    [Tooltip("Ronda mínima para que pueda aparecer el conejo (0-based, ej: 5 = ronda 6)")]
    [Range(0, 20)]
    public int minRoundForRabbit = 4; // No aparece en las primeras 5 rondas (0-4)

    [Tooltip("Número mínimo de apariciones del conejo por capítulo")]
    [Range(1, 5)]
    public int minRabbitAppearances = 2;

    [Tooltip("Número máximo de apariciones del conejo por capítulo")]
    [Range(1, 5)]
    public int maxRabbitAppearances = 3;

    [Tooltip("Tiempo que el conejo permanece activo antes de desaparecer (segundos)")]
    [Range(10f, 60f)]
    public float rabbitActiveTime = 30f;

    [Tooltip("Probabilidad base de aparición del conejo en rondas elegibles (0-1)")]
    [Range(0f, 1f)]
    public float rabbitSpawnChance = 0.4f;

    public static event Action<Transform, float> OnRabbitSpawned;
    public static event Action OnRabbitDespawned;

    // State
    public bool IsRunning { get; private set; }
    public int CurrentWaveIndex { get; private set; } = -1;
    public int LastCheckpointIndex { get; private set; } = -1;

    // Internals
    private System.Random rng;

    [Header("Reglas de avance")]
    [Tooltip("If enabled, the next wave will not start until all enemies from the current wave are cleared.")]
    public bool requireClearToAdvance = true;

    [Tooltip("Extra grace time (seconds) after reaching 0 alive enemies to avoid deactivation race conditions.")]
    public float waveClearGraceSeconds = 0.15f;

    [Tooltip("Hard timeout (0 = no limit). If exceeded, the wave will force-advance.")]
    public float waveClearTimeout = 0f; // optional

    // Internal wave alive tracking
    public int AliveInWave => _aliveInWave;
    private int _aliveInWave = 0;

    // Rabbit system state
    private List<int> _rabbitSpawnRounds = new List<int>();
    private bool _rabbitIsActive = false;
    private Coroutine _rabbitTimerCoroutine = null;
    private int _rabbitsSpawnedThisChapter = 0;
    private Vector3 _rabbitOriginalPosition;

    [Header("Debug Spawn")]
    public bool debugSpawn = false;
    [Range(1, 200)] public int debugMaxTries = 40;
    public bool debugUseNavMeshCheck = false;
    public float debugNavMeshMaxDistance = 0.8f;

    [Header("Spawn Telegraph")]
    [Tooltip("Retraso entre el telegraph y el spawn real del enemigo.")]
    public float telegraphDelay = 0.6f;

    private enum SpawnRejectReason { None, NoGroundHit, BlockedByOverlap, NavMeshInvalid }

    private struct SpawnAttempt
    {
        public Vector3 top;
        public Vector3 dir;
        public bool groundHit;
        public RaycastHit groundHitInfo;
        public bool diagHit;
        public RaycastHit diagHitInfo;
        public int overlapCount;
        public SpawnRejectReason reject;
    }

    private readonly List<SpawnAttempt> _debugAttempts = new List<SpawnAttempt>(256);
    private Vector3 _debugLastSuccess = Vector3.zero;

    [Header("Spawn Volume (capsule)")]
    [Tooltip("Capsule radius used to approximate the enemy volume when validating spawn placement.")]
    public float spawnCapsuleRadius = 0.30f;

    [Tooltip("Total capsule height (top-bottom).")]
    public float spawnCapsuleHeight = 1.80f;

    [Tooltip("Minimum ground offset to avoid initial penetration.")]
    public float spawnFootOffset = 0.05f;

    // ----------------------------------------------------
    #region Types

    [Serializable]
    public class SpawnStep
    {
        [EnemyAssetDropdown]
        [Tooltip("Selecciona el enemigo (ScriptableObject)")]
        public SinglePlayerEnemyAsset enemy;

        [Tooltip("Momento relativo (segundos) desde el inicio de la wave para este paso.")]
        public float atTime = 0f;

        [Tooltip("Pack size (1 = single spawn).")]
        [Range(1, 100)] public int packSize = 1;

        [Tooltip("Intervalo entre spawns dentro del paquete.")]
        public float intervalInsidePack = 0.3f;
    }

    [Serializable]
    public class Wave
    {
        [HideInInspector] public string label;
        [Tooltip("Delay antes de empezar la wave.")]
        public float preDelay = 1f;

        [Tooltip("Sequential steps defining the spawn order.")]
        public List<SpawnStep> steps = new List<SpawnStep>();

        [Tooltip("Eventos al empezar la wave (activar trampas, etc).")]
        public UnityEvent onWaveStart;

        [Tooltip("Marca esta wave como checkpoint.")]
        public bool isCheckpoint = false;
    }

    #endregion

    [Serializable] public class IntIntEvent : UnityEngine.Events.UnityEvent<int, int> { }

    [Header("UI / Wave Display")]
    [Tooltip("Se emite al empezar cada wave: (current, total).")]
    public IntIntEvent onWaveChanged;

    [Tooltip("Se emite al iniciar cada step: (stepIndex+1, stepsTotales)")]
    public IntIntEvent onStepAdvanced;

    public int TotalWaves => waves?.Count ?? 0;
    public int CurrentWaveNumber => (CurrentWaveIndex >= 0 ? CurrentWaveIndex + 1 : 0);

    [Header("Debug Shortcuts")]
    [Tooltip("If disabled, forces immediate return to pool (no animation). If enabled, tries to trigger the death sequence animation.")]
    public bool killViaAnimation = false;

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (waves == null) return;
        for (int i = 0; i < waves.Count; i++)
        {
            if (waves[i] == null) continue;
            waves[i].label = $"Wave {i + 1}" + (waves[i].isCheckpoint ? "  (Checkpoint)" : "");
        }
    }
#endif

    private void Awake()
    {
        if (singleton == null) singleton = this;
        else Destroy(this);

        rng = (rngSeed == -1) ? new System.Random() : new System.Random(rngSeed);

        _knownPools.Clear();
        foreach (var ea in enemyCatalog)
        {
            if (ea != null && !string.IsNullOrWhiteSpace(ea.poolName))
                _knownPools.Add(ea.poolName);
        }

        // Initialize rabbit system
        InitializeRabbitSystem();
    }

    private void OnDestroy()
    {
        // Clear singleton when destroyed
        if (singleton == this)
        {
            singleton = null;
            Debug.Log("[SinglePlayerWaveManager] Singleton cleared in OnDestroy");
        }

        StopAllCoroutines();
    }

    #region Waves Control

    public void StartWaves()
    {
        if (IsRunning || waves.Count == 0) return;
        LevelUpManager.singleton.IsSinglePlayer = true;

        StopAllCoroutines();
        IsRunning = true;

        StartCoroutine(RunWaves(fromIndex: 0, replayPriorEventsOnly: false));
    }

    /// <summary>
    /// Starts waves from the player's highest checkpoint (if available).
    /// If there are pending upgrades, shows the upgrade UI first and waits for completion.
    /// </summary>
    public void StartWavesWithCheckpointDetection()
    {
        if (IsRunning || waves.Count == 0) return;

        // First check if there are pending upgrades
        if (LevelUpManager.singleton != null && LevelUpManager.singleton.HasPendingUpgrades())
        {
            Debug.Log($"[SinglePlayerWaveManager] There are {LevelUpManager.singleton.GetPendingUpgrades()} pending upgrades. Showing UI before starting waves.");
            LevelUpManager.singleton.ForceShowUpgradeUI();

            StartCoroutine(WaitForUpgradesAndStartWaves());
            return;
        }

        StartWavesWithCheckpointDetectionInternal();
    }

    /// <summary>
    /// Waits until pending upgrades are completed and then starts waves.
    /// </summary>
    private IEnumerator WaitForUpgradesAndStartWaves()
    {
        while (LevelUpManager.singleton != null && LevelUpManager.singleton.HasPendingUpgrades())
        {
            yield return new WaitForSeconds(0.1f);
        }

        Debug.Log("[SinglePlayerWaveManager] Upgrades completed. Starting waves.");
        StartWavesWithCheckpointDetectionInternal();
    }

    private void StartWavesWithCheckpointDetectionInternal()
    {
        int checkpointWaveIndex = GetPlayerHighestCheckpointWaveIndex();

        // Check for checkpoint upgrade rewards (project-specific)
        CheckForCheckpointUpgrades(checkpointWaveIndex);

        if (checkpointWaveIndex > 0)
        {
            Debug.Log($"[SinglePlayerWaveManager] Starting from checkpoint at wave {checkpointWaveIndex + 1}");
            StartFromCheckpoint(checkpointWaveIndex, true);
        }
        else
        {
            Debug.Log("[SinglePlayerWaveManager] No checkpoints available, starting from the beginning");
            StartWaves();
        }
    }

    public void StartFromCheckpoint(int checkpointWaveIndex, bool replayEventsOfPreviousWaves = true)
    {
        if (checkpointWaveIndex < 0 || checkpointWaveIndex >= waves.Count)
        {
            Debug.LogWarning($"[SinglePlayerWaveManager] Invalid checkpoint index: {checkpointWaveIndex}");
            StartWaves();
            return;
        }

        StopAllCoroutines();
        IsRunning = true;
        LastCheckpointIndex = checkpointWaveIndex;

        StartCoroutine(RunWaves(fromIndex: checkpointWaveIndex, replayPriorEventsOnly: replayEventsOfPreviousWaves));
    }

    public void StopWaves()
    {
        StopAllCoroutines();
        IsRunning = false;
        CurrentWaveIndex = -1;

        // Clean up rabbit if active
        CleanupCurrentRabbit();
    }

    public void StartFromCheckpoint(bool replayEventsOfPreviousWaves = true)
    {
        int start = LastCheckpointIndex >= 0 ? LastCheckpointIndex : GetLatestCheckpointIndex();
        if (start < 0) start = 0;

        StopAllCoroutines();
        IsRunning = true;

        StartCoroutine(RunWaves(fromIndex: start, replayPriorEventsOnly: replayEventsOfPreviousWaves));
    }

    public int GetLatestCheckpointIndex()
    {
        int idx = -1;
        for (int i = 0; i < waves.Count; i++)
        {
            if (waves[i].isCheckpoint) idx = i;
        }
        return idx;
    }

    private int GetPlayerHighestCheckpointWaveIndex()
    {
        if (SinglePlayerManager.singleton == null || SinglePlayerManager.singleton.singlePlayerData == null)
        {
            Debug.LogWarning("[SinglePlayerWaveManager] No SinglePlayerManager data available");
            return -1;
        }

        if (GameProgressTracker.ShouldIgnoreSavedProgress())
        {
            Debug.Log("[SinglePlayerWaveManager] Ignoring saved progress: starting chapter from the beginning");
            return -1;
        }

        var singlePlayerInfo = SinglePlayerManager.singleton.singlePlayerData.singleplayer;
        if (singlePlayerInfo == null)
        {
            Debug.LogWarning("[SinglePlayerWaveManager] No singleplayer info available");
            return -1;
        }

        int currentChapterId = 1;
        if (GameProgressTracker.Instance != null)
        {
            currentChapterId = GameProgressTracker.Instance.chapterID;
        }

        var playerProgress = System.Array.Find(singlePlayerInfo.progress, p => p.chapter_id == currentChapterId);
        if (playerProgress == null)
        {
            Debug.Log($"[SinglePlayerWaveManager] No progress for chapter {currentChapterId}");
            return -1;
        }

        var chapterInfo = System.Array.Find(singlePlayerInfo.chapterlist, c => c.id == currentChapterId);
        if (chapterInfo == null || chapterInfo.checkpoints == null)
        {
            Debug.LogWarning($"[SinglePlayerWaveManager] No checkpoint info found for chapter {currentChapterId}");
            return -1;
        }

        int highestCheckpointWave = -1;
        int playerWavesCompleted = playerProgress.waves_completed;

        Debug.Log($"[SinglePlayerWaveManager] Player completed {playerWavesCompleted} waves in chapter {currentChapterId}");

        foreach (var checkpoint in chapterInfo.checkpoints)
        {
            if (playerWavesCompleted >= checkpoint.checkpoint_wave)
            {
                int waveIndex = checkpoint.checkpoint_wave - 1;

                if (waveIndex >= 0 && waveIndex < waves.Count && waves[waveIndex].isCheckpoint)
                {
                    if (waveIndex > highestCheckpointWave)
                    {
                        highestCheckpointWave = waveIndex;
                        Debug.Log($"[SinglePlayerWaveManager] Checkpoint available at wave {waveIndex + 1}");
                    }
                }
            }
        }

        return highestCheckpointWave;
    }

    private void CheckForCheckpointUpgrades(int checkpointWaveIndex)
    {
        if (checkpointWaveIndex < 0 || LevelUpManager.singleton == null)
        {
            Debug.Log("[SinglePlayerWaveManager] No checkpoint or LevelUpManager available");
            return;
        }

        if (SinglePlayerManager.singleton?.singlePlayerData?.singleplayer?.chapterlist != null)
        {
            int currentChapterId = GameProgressTracker.Instance?.chapterID ?? 1;
            var chapterInfo = System.Array.Find(
                SinglePlayerManager.singleton.singlePlayerData.singleplayer.chapterlist,
                c => c.id == currentChapterId
            );

            if (chapterInfo.checkpoints != null)
            {
                int checkpointWave = checkpointWaveIndex + 1;
                var checkpoint = System.Array.Find(
                    chapterInfo.checkpoints,
                    c => c.checkpoint_wave == checkpointWave
                );

                if (checkpoint.id != 0 && checkpoint.upgrades_available > 0)
                {
                    Debug.Log($"[SinglePlayerWaveManager] Checkpoint wave {checkpointWave} has {checkpoint.upgrades_available} upgrades available");
                    LevelUpManager.singleton.AddPendingUpgrades(checkpoint.upgrades_available);
                }
                else if (checkpoint.id != 0)
                {
                    Debug.Log($"[SinglePlayerWaveManager] Checkpoint wave {checkpointWave} has no upgrades available");
                }
                else
                {
                    Debug.LogWarning($"[SinglePlayerWaveManager] No checkpoint found for wave {checkpointWave}");
                }
            }
        }
    }

    #endregion

    #region Wave Cheats

    private void Debug_AdvanceWaveOne()
    {
        StopAllCoroutines();
        Debug_KillAllAliveUnits();

        // (EN) Compute the next wave index.
        int next = Mathf.Clamp(CurrentWaveIndex + 1, 0, Mathf.Max(0, waves.Count - 1));

        IsRunning = true;
        StartCoroutine(RunWaves(fromIndex: next, replayPriorEventsOnly: false));
    }

    // (EN) Jump to the next wave marked as a checkpoint after the current one.
    private void Debug_JumpToNextCheckpoint()
    {
        StopAllCoroutines();
        Debug_KillAllAliveUnits();

        // (EN) Find next checkpoint (> CurrentWaveIndex).
        int target = GetNextCheckpointIndex(CurrentWaveIndex);
        if (target < 0)
        {
            Debug.LogWarning("[WaveManager] No more checkpoints ahead.");
            return;
        }

        IsRunning = true;
        StartCoroutine(RunWaves(fromIndex: target, replayPriorEventsOnly: true));
    }

    // (EN) Returns the index of the next checkpoint strictly after 'fromExclusive'.
    private int GetNextCheckpointIndex(int fromExclusive)
    {
        for (int i = fromExclusive + 1; i < waves.Count; i++)
            if (waves[i].isCheckpoint) return i;
        return -1;
    }

    private void Debug_KillAllAliveUnits()
    {
        // (EN) To avoid race conditions, reset the counter (OnDisable will also decrement as units disable).
        _aliveInWave = 0;

        var units = FindObjectsOfType<WaveUnit>(includeInactive: true);
        int count = 0;

        foreach (var wu in units)
        {
            if (wu == null || wu.gameObject == null) continue;
            if (!wu.gameObject.activeInHierarchy) continue;

            // (EN) Attempt #1: "nice" death via animation (if requested).
            if (killViaAnimation)
            {
                var md = wu.GetComponent<Solo.MOST_IN_ONE.Most_Damage>();
                if (md != null)
                {
                    // (EN) Start the death sequence; if you have animation events for pooling, it will return when finished.
                    var m = md.GetType().GetMethod(
                        "StartDeathSequence",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    );
                    if (m != null) { m.Invoke(md, null); count++; continue; }
                }
            }

            // (EN) Attempt #2: immediate return to pool (more reliable for debug).
            var ret = wu.GetComponentInChildren<ReturnObjectToPool>(true);
            if (ret != null)
            {
                ret.ReturnObjectToPoolFunc(0f);
                count++;
                continue;
            }

            wu.gameObject.SetActive(false);
            count++;
        }

        if (count > 0) Debug.Log($"[WaveManager] Debug_KillAllAliveUnits -> {count} units removed.");
    }

    #endregion

    #region Core

    private IEnumerator RunWaves(int fromIndex, bool replayPriorEventsOnly)
    {
        // (EN) Optionally "replay" prior wave start events (without spawning) to rebuild context.
        if (replayPriorEventsOnly && fromIndex > 0)
        {
            for (int i = 0; i < fromIndex; i++)
            {
                waves[i].onWaveStart?.Invoke();
                yield return null;
            }
        }

        for (int w = fromIndex; w < waves.Count; w++)
        {
            CurrentWaveIndex = w;
            var wave = waves[w];

            if (wave.preDelay > 0f)
                yield return new WaitForSeconds(wave.preDelay);

            onWaveChanged?.Invoke(CurrentWaveIndex + 1, TotalWaves);

            _aliveInWave = 0;

            wave.onWaveStart?.Invoke();
            if (wave.isCheckpoint) LastCheckpointIndex = w;

            CheckAndSpawnRabbit(w);

            wave.steps.Sort((a, b) => a.atTime.CompareTo(b.atTime));

            float elapsed = 0f;
            int nextStep = 0;

            if (wave.steps.Count == 0)
            {
                // (EN) If there are no steps, still wait/clear in case onWaveStart spawned enemies.
                if (requireClearToAdvance)
                    yield return WaitForWaveClearOrTimeout();
                continue;
            }

            while (nextStep < wave.steps.Count)
            {
                var step = wave.steps[nextStep];

                float wait = Mathf.Max(0f, step.atTime - elapsed);
                if (wait > 0f)
                {
                    elapsed += wait;
                    yield return new WaitForSeconds(wait);
                }

                onStepAdvanced?.Invoke(nextStep + 1, wave.steps.Count);

                yield return StartCoroutine(SpawnPack(step));
                nextStep++;
            }

            if (requireClearToAdvance)
                yield return WaitForWaveClearOrTimeout();

            if (GameProgressTracker.Instance != null)
            {
                GameProgressTracker.Instance.RegisterWaveCompleted(CurrentWaveIndex + 1);
            }
        }
    }

    private IEnumerator WaitForWaveClearOrTimeout()
    {
        float start = Time.time;

        while (_aliveInWave > 0)
        {
            if (waveClearTimeout > 0f && Time.time - start > waveClearTimeout)
            {
                Debug.LogWarning("[WaveManager] Wave clear timeout reached. Forcing advance.");
                break;
            }
            yield return null;
        }

        // (EN) Small grace period to avoid deactivation race conditions.
        if (waveClearGraceSeconds > 0f)
            yield return new WaitForSeconds(waveClearGraceSeconds);
    }

    private IEnumerator SpawnPack(SpawnStep step)
    {
        if (step.enemy == null || string.IsNullOrWhiteSpace(step.enemy.poolName))
        {
            Debug.LogWarning("[WaveManager] SpawnStep missing EnemyAsset or poolName.");
            yield break;
        }

        // If there is no interval, spawn all coroutines in parallel
        if (step.intervalInsidePack <= 0f)
        {
            for (int i = 0; i < step.packSize; i++)
            {
                if (TryFindSpawnPositionInRing(out var pos))
                    StartCoroutine(SpawnOneWithTelegraph(step.enemy.poolName, pos));
                else
                    Debug.LogWarning("[WaveManager] No valid spawn position found.");
            }

            if (telegraphDelay > 0f) yield return new WaitForSeconds(telegraphDelay);
            yield break;
        }

        // Sequential pack spawn when interval is set
        for (int i = 0; i < step.packSize; i++)
        {
            if (TryFindSpawnPositionInRing(out var pos))
                StartCoroutine(SpawnOneWithTelegraph(step.enemy.poolName, pos));
            else
                Debug.LogWarning("[WaveManager] No valid spawn position found.");

            if (i < step.packSize - 1 && step.intervalInsidePack > 0f)
                yield return new WaitForSeconds(step.intervalInsidePack);
        }
    }

    #endregion

    #region Spawn Helpers

    private GameObject SpawnFromPool(string poolName, Vector3 worldPos)
    {
        var pooled = PoolingManager.singleton?.GetObjectFromPool(pooledParent, pooledSiblingIndex, poolName);
        if (pooled == null)
        {
            Debug.LogWarning($"[WaveManager] Pool '{poolName}' not available.");
            return null;
        }

        pooled.transform.position = worldPos;

        // Reset reusable pooled object state
        var md = pooled.GetComponent<Solo.MOST_IN_ONE.Most_Damage>();
        if (md != null) md.ResetForReuse();

        var be = pooled.GetComponent<BaseEnemy>();
        if (be != null) be.ResetForReuse_Base();

        // Ensure WaveUnit exists and can notify this manager when it disables
        var wu = pooled.GetComponent<WaveUnit>();
        if (wu == null) wu = pooled.gameObject.AddComponent<WaveUnit>();
        wu.owner = this;
        wu.waveIndex = CurrentWaveIndex;

        _aliveInWave++;
        return pooled;
    }

    private bool TryFindSpawnPositionInRing(out Vector3 result)
    {
        result = Vector3.zero;
        if (ResolvePlayer() == null)
        {
            Debug.LogWarning("[WaveManager] Player not assigned.");
            return false;
        }

        _debugAttempts.Clear();

        float rMin = Mathf.Min(minRadius, maxRadius);
        float rMax = Mathf.Max(minRadius, maxRadius);
        if (Mathf.Approximately(rMax, 0f) || rMax <= rMin + 0.1f)
            rMax = rMin + 0.1f;

        int tries = debugSpawn ? Mathf.Max(maxPlacementTries, debugMaxTries) : maxPlacementTries;

        for (int t = 0; t < tries; t++)
        {
            // Uniform sampling inside a ring (area-uniform)
            float ang = (float)(rng.NextDouble() * Mathf.PI * 2f);
            float u = (float)rng.NextDouble();
            float r = Mathf.Sqrt(u * (rMax * rMax - rMin * rMin) + rMin * rMin);

            Vector3 flat = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;
            Vector3 top = player.position + Vector3.up * spawnCastHeight + flat;
            Vector3 dir = Vector3.down;

            var attempt = new SpawnAttempt { top = top, dir = dir, groundHit = false, diagHit = false, overlapCount = 0, reject = SpawnRejectReason.None };

            // 1) Ground raycast (groundMask)
            if (Physics.Raycast(top, dir, out RaycastHit hit, Mathf.Infinity, groundMask, QueryTriggerInteraction.Ignore))
            {
                attempt.groundHit = true;
                attempt.groundHitInfo = hit;
                Vector3 p = hit.point;

                // (EN) Optional NavMesh validation (editor/debug only).
#if UNITY_AI_NAVIGATION
                if (debugUseNavMeshCheck)
                {
                    if (!UnityEngine.AI.NavMesh.SamplePosition(p, out var navHit, debugNavMeshMaxDistance, UnityEngine.AI.NavMesh.AllAreas))
                    {
                        attempt.reject = SpawnRejectReason.NavMeshInvalid;
                        _debugAttempts.Add(attempt);
                        continue;
                    }
                    p = navHit.position;
                }
#endif

                // (EN) Approximate the enemy volume with a vertical capsule for spawn validation.
                Vector3 basePos = p + Vector3.up * (spawnFootOffset + spawnCapsuleRadius);
                Vector3 topPos = basePos + Vector3.up * (spawnCapsuleHeight - 2f * spawnCapsuleRadius);

                // (EN) Does it overlap anything in "blockedBy" (walls/props/etc.)?
                bool blockedCapsule = Physics.CheckCapsule(
                    basePos,
                    topPos,
                    spawnCapsuleRadius,
                    blockedBy,
                    QueryTriggerInteraction.Ignore
                );

                if (blockedCapsule)
                    continue;

                // (EN) Success.
                _debugAttempts.Add(attempt);
                _debugLastSuccess = p;
                result = p;
                return true;
            }
            else
            {
                // (EN) Diagnostic raycast without mask to see what is below.
                if (Physics.Raycast(top, dir, out RaycastHit diag, Mathf.Infinity, ~0, QueryTriggerInteraction.Ignore))
                {
                    attempt.diagHit = true;
                    attempt.diagHitInfo = diag;
                }

                attempt.reject = SpawnRejectReason.NoGroundHit;
                _debugAttempts.Add(attempt);
            }
        }

        // (EN) If we failed to find a valid point, dump detailed debug info.
        if (debugSpawn) DumpSpawnDebug();
        return false;
    }

    private void DumpSpawnDebug()
    {
        int total = _debugAttempts.Count;
        int noGround = 0, blocked = 0, navBad = 0;

        foreach (var a in _debugAttempts)
        {
            switch (a.reject)
            {
                case SpawnRejectReason.NoGroundHit: noGround++; break;
                case SpawnRejectReason.BlockedByOverlap: blocked++; break;
                case SpawnRejectReason.NavMeshInvalid: navBad++; break;
            }
        }

        Debug.LogWarning($"[SpawnDebug] Attempts={total}  NoGround={noGround}  Blocked={blocked}  NavBad={navBad}  groundMask={LayerMaskToString(groundMask)}  blockedBy={LayerMaskToString(blockedBy)}");

        // (EN) Print the first 10 attempts with useful details.
        int maxShow = Mathf.Min(10, total);
        for (int i = 0; i < maxShow; i++)
        {
            var a = _debugAttempts[i];
            string reason = a.reject.ToString();
            string ground = a.groundHit ? $"GROUND hit {a.groundHitInfo.point} dist={a.groundHitInfo.distance:F2}" : "GROUND miss";
            string diag = a.diagHit ? $"DIAG hit layer={LayerMask.LayerToName(a.diagHitInfo.collider.gameObject.layer)} at {a.diagHitInfo.point}" : "DIAG none";
            string overlaps = $"overlaps={a.overlapCount}";
            Debug.Log($"[SpawnDebug] #{i + 1}: {reason} | {ground} | {diag} | {overlaps}");
        }
    }

    private static string LayerMaskToString(LayerMask mask)
    {
        if (mask.value == 0) return "(None)";
        List<string> names = new List<string>();
        for (int i = 0; i < 32; i++)
            if ((mask.value & (1 << i)) != 0)
                names.Add(LayerMask.LayerToName(i));
        return string.Join(",", names);
    }

    private void OnDrawGizmosSelected()
    {
        if (!debugSpawn || _debugAttempts == null) return;

        foreach (var a in _debugAttempts)
        {
            Color c = Color.white;
            switch (a.reject)
            {
                case SpawnRejectReason.NoGroundHit: c = Color.yellow; break;
                case SpawnRejectReason.BlockedByOverlap: c = Color.red; break;
                case SpawnRejectReason.NavMeshInvalid: c = Color.cyan; break;
                case SpawnRejectReason.None: c = Color.green; break;
            }

            if (a.groundHit)
            {
                Gizmos.color = c;
                Gizmos.DrawSphere(a.groundHitInfo.point, 0.2f);
                if (spawnClearRadius > 0f)
                {
                    Gizmos.color = new Color(c.r, c.g, c.b, 0.25f);
                    Gizmos.DrawWireSphere(a.groundHitInfo.point, spawnClearRadius);
                }
            }
            else
            {
                if (a.diagHit)
                {
                    // (EN) Draw a line to where it would have hit without a mask (if we have diagnostic hit info).
                    Gizmos.color = Color.gray;
                    Gizmos.DrawLine(a.top, a.diagHitInfo.point);
                    Gizmos.DrawSphere(a.diagHitInfo.point, 0.15f);
                }
            }
        }

        if (_debugLastSuccess != Vector3.zero)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(_debugLastSuccess, 0.35f);
        }

#if UNITY_EDITOR
        if (player != null)
        {
            Handles.color = new Color(0, 1, 1, 0.2f);
            Handles.DrawWireDisc(player.position, Vector3.up, minRadius);
            Handles.DrawWireDisc(player.position, Vector3.up, maxRadius);
        }
#endif
    }

#if UNITY_EDITOR
    [ContextMenu("Spawn Debug / Test 100 samples here")]
    private void __SpawnDebug_Test()
    {
        debugSpawn = true;
        debugMaxTries = Mathf.Max(debugMaxTries, 100);
        _debugAttempts.Clear();

        // (EN) Runs a single "dry" sample (does not spawn anything), only to populate _debugAttempts.
        Vector3 _;
        TryFindSpawnPositionInRing(out _);

        DumpSpawnDebug(); // (EN) Force dump regardless of success/failure.
        Selection.activeObject = this.gameObject;
    }
#endif

    private IEnumerator SpawnOneWithTelegraph(string poolName, Vector3 worldPos)
    {
        // 1) Spawn telegraph FX (pool)
        var pm = PoolingManager.singleton;
        if (pm != null && telegraphDelay > 0f)
        {
            var fxGo = pm.GetObjectFromPool(null, pooledSiblingIndex, "EXAMPLE_SPAWN_FX_POOL"); // anonymized
            if (fxGo != null)
            {
                var fx = fxGo.GetComponent<SpawnFXPooled>();
                if (fx != null) fx.PlayAt(worldPos, telegraphDelay);
                else { fxGo.transform.position = worldPos; pm.ReturnObjectToPool(fxGo, "EXAMPLE_SPAWN_FX_POOL"); }
            }
        }

        // (EN) Wait for the telegraph "cast time".
        if (telegraphDelay > 0f)
            yield return new WaitForSeconds(telegraphDelay);

        // 3) Spawn enemy from its pool
        var enemyGo = SpawnFromPool(poolName, worldPos);

        // (EN) Make it "rise from the ground" without relying on an Animator.
        if (enemyGo != null)
        {
            ApplyWaveScaling(enemyGo);

            var rise = enemyGo.GetComponent<SpawnRiseSimple>();
            if (rise == null) rise = enemyGo.AddComponent<SpawnRiseSimple>(); // (EN) Fallback if the prefab is missing this component.
            rise.Play(worldPos);
        }
    }

    #endregion

    #region Enemy Scale

    [Header("Enemy Scaling (Linear by Wave)")]
    [Range(0, 100)] public float speedMinPercent = 0f;
    [Range(0, 100)] public float speedMaxPercent = 50f;

    [Range(0, 300)] public float healthMinPercent = 0f;
    [Range(0, 300)] public float healthMaxPercent = 50f;

    public bool scaleDamage = false;
    [Range(0, 300)] public float damageMinPercent = 0f;
    [Range(0, 300)] public float damageMaxPercent = 50f;

    [Tooltip("Enemigos a los que NO se aplicará el escalado")]
    public List<EnemyID> excludedEnemyIDs = new();

    public bool debugScaling = false;

    private float WaveT01(int waveIndex)
    {
        int total = waves.Count;
        if (total <= 1) return 1f;
        return Mathf.Clamp01((float)waveIndex / (float)(total - 1));
    }

    private float PercentToMult(float percent) => 1f + (percent * 0.01f);

    private (float spdMult, float hpMult, float dmgMult) GetMultipliersForWave(int waveIndex)
    {
        float t = WaveT01(waveIndex);
        float spdPct = Mathf.Lerp(speedMinPercent, speedMaxPercent, t);
        float hpPct = Mathf.Lerp(healthMinPercent, healthMaxPercent, t);
        float dmgPct = scaleDamage ? Mathf.Lerp(damageMinPercent, damageMaxPercent, t) : 0f;

        return (PercentToMult(spdPct), PercentToMult(hpPct), PercentToMult(dmgPct));
    }

    private void ApplyWaveScaling(GameObject enemyGo)
    {
        if (enemyGo == null) return;

        var be = enemyGo.GetComponent<BaseEnemy>();
        if (be != null && excludedEnemyIDs.Contains(be.enemyID)) return;

        var cache = enemyGo.GetComponent<EnemyWaveScalingCache>();
        if (cache == null) cache = enemyGo.AddComponent<EnemyWaveScalingCache>();
        cache.EnsureCache();

        var (spdMult, hpMult, dmgMult) = GetMultipliersForWave(CurrentWaveIndex);

        // Speed
        if (be != null)
        {
            float newSpeed = cache.baseSpeed * spdMult;
            be.movementSpeed = newSpeed;

            var agent = enemyGo.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent && agent.enabled) agent.speed = newSpeed;
        }

        // Health
        var md = enemyGo.GetComponent<Solo.MOST_IN_ONE.Most_Damage>();
        if (md != null)
        {
            float newMax = cache.baseMaxHealth * hpMult;
            md.MaxHealth = newMax;
            md.Health = newMax;
        }

        // Damage (optional)
        if (scaleDamage && be != null)
        {
            int newDamage = Mathf.CeilToInt(cache.baseDamageToPlayer * dmgMult);
            be.damageToPlayer = Mathf.Max(1, newDamage);
        }

        if (debugScaling)
        {
            Debug.Log($"[WaveScale] Wave {CurrentWaveIndex + 1}/{waves.Count} " +
                      $"SPD x{spdMult:0.00} HP x{hpMult:0.00} " +
                      $"{(scaleDamage ? $"DMG x{dmgMult:0.00}" : "DMG = base")} :: " +
                      $"{enemyGo.name}");
        }
    }

    #endregion

    public void NotifyUnitGone(WaveUnit wu)
    {
        if (wu != null)
        {
            if (_aliveInWave > 0) _aliveInWave--;
        }
    }

    public void ChangeScene()
    {
        string sceneToUnload = GameProgressTracker.Instance != null
            ? GameProgressTracker.Instance.CurrentSceneName
            : null;

        if (!string.IsNullOrEmpty(sceneToUnload))
        {
            SceneLoader.singleton.UnloadScene(sceneToUnload);
            GameProgressTracker.ClearCurrentSceneName();
        }
        else
        {
            SceneLoader.singleton.UnloadScene("EXAMPLE_GAMEPLAY_SCENE"); // anonymized
        }

        bool handledOrientation = false;

        if (SinglePlayerManager.singleton != null)
        {
            SinglePlayerManager.singleton.LoadHubScene();
            handledOrientation = true;
        }
        else
        {
            SceneLoader.singleton.LoadSceneAdditive("EXAMPLE_HUB_SCENE", false); // anonymized
        }

        if (!handledOrientation)
        {
            OrientationUtil.ForcePortrait(this);
        }
    }

    private Transform ResolvePlayer()
    {
        if (player != null) return player;
        var go = GameObject.FindWithTag("Player");
        if (go != null) player = go.transform;
        return player;
    }

#if UNITY_EDITOR
    [ContextMenu("Debug/Public: Advance Wave One")]
    public void Debug_PublicAdvanceWaveOne()
    {
        Debug_AdvanceWaveOne();
    }

    [ContextMenu("Debug/Public: Jump to Next Checkpoint")]
    public void Debug_PublicJumpToNextCheckpoint()
    {
        Debug_JumpToNextCheckpoint();
    }
#endif

    #region Rabbit Collectible System

    private void InitializeRabbitSystem()
    {
        _rabbitSpawnRounds.Clear();
        _rabbitsSpawnedThisChapter = 0;
        _rabbitIsActive = false;

        if (collectableRabbit == null)
        {
            Debug.LogWarning("[WaveManager] No collectible rabbit reference set.");
            return;
        }

        _rabbitOriginalPosition = collectableRabbit.transform.position;
        collectableRabbit.SetActive(false);

        if (waves == null || waves.Count <= minRoundForRabbit)
        {
            Debug.LogWarning("[WaveManager] Not enough waves for rabbit system.");
            return;
        }

        int targetAppearances = rng.Next(minRabbitAppearances, maxRabbitAppearances + 1);

        List<int> eligibleRounds = new List<int>();
        for (int i = minRoundForRabbit; i < waves.Count; i++)
        {
            eligibleRounds.Add(i);
        }

        for (int i = 0; i < targetAppearances && eligibleRounds.Count > 0; i++)
        {
            int randomIndex = rng.Next(eligibleRounds.Count);
            int selectedRound = eligibleRounds[randomIndex];
            _rabbitSpawnRounds.Add(selectedRound);
            eligibleRounds.RemoveAt(randomIndex);
        }

        _rabbitSpawnRounds.Sort();

        Debug.Log($"[WaveManager] Rabbit scheduled rounds: {string.Join(", ", _rabbitSpawnRounds.ConvertAll(r => (r + 1).ToString()))}");
    }

    private void CheckAndSpawnRabbit(int currentWaveIndex)
    {
        if (collectableRabbit == null) return;

        if (!_rabbitSpawnRounds.Contains(currentWaveIndex))
            return;

        if (rng.NextDouble() > rabbitSpawnChance)
        {
            Debug.Log($"[WaveManager] Rabbit scheduled for wave {currentWaveIndex + 1} but failed chance ({rabbitSpawnChance:P0})");
            return;
        }

        CleanupCurrentRabbit();

        if (TryFindSpawnPositionInRing(out Vector3 spawnPos))
        {
            StartCoroutine(ActivateRabbitWithDelay(spawnPos, currentWaveIndex));
        }
        else
        {
            Debug.LogWarning($"[WaveManager] Could not find a valid spawn position for rabbit on wave {currentWaveIndex + 1}");
        }
    }

    private IEnumerator ActivateRabbitWithDelay(Vector3 spawnPos, int waveIndex)
    {
        yield return new WaitForSeconds(2f);

        if (_rabbitIsActive || collectableRabbit == null)
            yield break;

        collectableRabbit.transform.position = spawnPos;
        collectableRabbit.SetActive(true);
        _rabbitIsActive = true;
        _rabbitsSpawnedThisChapter++;

        float rabbitEndTime = Time.time + rabbitActiveTime;
        OnRabbitSpawned?.Invoke(collectableRabbit.transform, rabbitEndTime);

        Debug.Log($"[WaveManager] Rabbit #{_rabbitsSpawnedThisChapter} activated on wave {waveIndex + 1} at {spawnPos}");

        _rabbitTimerCoroutine = StartCoroutine(RabbitDisappearTimer());
        SetupRabbitComponents(collectableRabbit);
    }

    private void SetupRabbitComponents(GameObject rabbit)
    {
        var collectible = rabbit.GetComponent<CollectibleRabbit>();
        if (collectible != null)
        {
            collectible.ResetState();
            collectible.OnCollected += OnRabbitCollected;
        }
    }

    private IEnumerator RabbitDisappearTimer()
    {
        yield return new WaitForSeconds(rabbitActiveTime);

        if (_rabbitIsActive && collectableRabbit != null)
        {
            Debug.Log($"[WaveManager] Rabbit despawned by timeout ({rabbitActiveTime}s)");
            CleanupCurrentRabbit();
        }
    }

    private void OnRabbitCollected()
    {
        Debug.Log("[WaveManager] Rabbit collected!");
        CleanupCurrentRabbit();
    }

    private void CleanupCurrentRabbit()
    {
        if (_rabbitTimerCoroutine != null)
        {
            StopCoroutine(_rabbitTimerCoroutine);
            _rabbitTimerCoroutine = null;
        }

        if (_rabbitIsActive && collectableRabbit != null)
        {
            var collectible = collectableRabbit.GetComponent<CollectibleRabbit>();
            if (collectible != null)
            {
                collectible.OnCollected -= OnRabbitCollected;
            }

            collectableRabbit.transform.position = _rabbitOriginalPosition;
            collectableRabbit.SetActive(false);
            _rabbitIsActive = false;
            OnRabbitDespawned?.Invoke();
        }
    }

    [ContextMenu("Debug: Activate Rabbit Now")]
    public void Debug_ActivateRabbitNow()
    {
        if (TryFindSpawnPositionInRing(out Vector3 spawnPos))
        {
            StartCoroutine(ActivateRabbitWithDelay(spawnPos, CurrentWaveIndex));
        }
    }

    public string GetRabbitSystemInfo()
    {
        return $"Spawned: {_rabbitsSpawnedThisChapter}, Scheduled waves: [{string.Join(", ", _rabbitSpawnRounds.ConvertAll(r => (r + 1).ToString()))}], Active: {_rabbitIsActive}";
    }

    #endregion
}

public class WaveUnit : MonoBehaviour
{
    [HideInInspector] public SinglePlayerWaveManager owner;
    [HideInInspector] public int waveIndex;

    private void OnDisable()
    {
        // Notify manager when this object returns to pool or gets disabled.
        if (owner != null) owner.NotifyUnitGone(this);
    }
}
