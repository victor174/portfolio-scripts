using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

/// <summary>
/// Singleton manager responsible for loading Sprites via Unity Addressables.
///
/// Key points:
/// - Uses a simple in-memory cache (AddressablesCache) to reuse already-loaded sprites.
/// - Loads assets asynchronously using Addressables and returns the Sprite (or null if it fails).
///
/// Notes:
/// - This is intentionally lightweight for UI/icon loading scenarios.
/// - The cache release policy is handled elsewhere (e.g., AddressablesCache.ClearAndRelease()).
/// </summary>
public class ImageManagerBundle : MonoBehaviour
{
    private static ImageManagerBundle _instance;

    /// <summary>
    /// Singleton instance accessor.
    /// If no instance exists in the scene, it creates one and marks it as DontDestroyOnLoad.
    /// </summary>
    public static ImageManagerBundle Instance
    {
        get
        {
            if (_instance == null)
            {
                // Look for an existing instance in the scene.
                _instance = FindObjectOfType<ImageManagerBundle>();

                if (_instance == null)
                {
                    // If none exists, create a new GameObject and attach this component.
                    GameObject obj = new GameObject("ImageManagerBundle");
                    _instance = obj.AddComponent<ImageManagerBundle>();

                    // Optional: persist across scene loads (useful for global UI systems).
                    DontDestroyOnLoad(obj);
                }
            }

            return _instance;
        }
    }

    /// <summary>
    /// Ensures the singleton pattern is enforced at runtime.
    /// If another instance already exists, this object is destroyed.
    /// </summary>
    private void Awake()
    {
        if (_instance == null)
        {
            _instance = this;

            // Optional: persist across scene loads.
            DontDestroyOnLoad(gameObject);
        }
        else if (_instance != this)
        {
            // Prevent duplicates.
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// Asynchronously loads a Sprite using Addressables.
    /// - If the sprite is cached, returns it immediately.
    /// - Otherwise, loads it and stores the handle in the cache.
    /// </summary>
    /// <param name="key">Addressables key for the Sprite.</param>
    /// <returns>The loaded Sprite, or null if loading fails.</returns>
    public async Task<Sprite> CargarSprite(string key)
    {
        // Fast path: return cached sprite if already loaded.
        if (AddressablesCache.Contains(key))
        {
            var cachedHandle = AddressablesCache.Get(key);
            return cachedHandle.Result as Sprite;
        }

        // Load via Addressables.
        var loadHandle = Addressables.LoadAssetAsync<Sprite>(key);
        var tcs = new TaskCompletionSource<Sprite>();

        // Convert Addressables callback-style completion into an awaitable Task.
        loadHandle.Completed += handle =>
        {
            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                // Cache the handle so future calls reuse the already-loaded asset.
                AddressablesCache.Add(key, handle);
                tcs.SetResult(handle.Result);
            }
            else
            {
                // Keep logs informative: key + exception detail.
                Debug.LogError($"[Addressables] Failed to load Sprite with key: {key}\n{handle.OperationException}");
                tcs.SetResult(null);
            }
        };

        return await tcs.Task;
    }
}
