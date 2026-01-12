using System.Collections.Generic;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

/// <summary>
/// Minimal Addressables handle cache.
/// 
/// Purpose:
/// - Store AsyncOperationHandles by a string key to reuse loaded assets.
/// - Provide a single place to release handles when the cache is cleared.
/// 
/// Notes:
/// - Handles should be released via Addressables.Release(...) when no longer needed,
///   otherwise you can leak memory / keep references alive.
/// - This cache is intentionally simple and assumes it is used from Unity's main thread.
/// </summary>
public static class AddressablesCache
{
    // Stores Addressables operation handles (load results, etc.) by key.
    private static Dictionary<string, AsyncOperationHandle> cache = new Dictionary<string, AsyncOperationHandle>();

    /// <summary>
    /// Adds a handle to the cache only if the key is not already present.
    /// This prevents overwriting an existing cached handle.
    /// </summary>
    public static void Add(string key, AsyncOperationHandle handle)
    {
        if (!cache.ContainsKey(key))
            cache[key] = handle;
    }

    /// <summary>
    /// Returns true if a handle exists in the cache for the given key.
    /// </summary>
    public static bool Contains(string key)
    {
        return cache.ContainsKey(key);
    }

    /// <summary>
    /// Gets the cached handle for the given key.
    /// IMPORTANT: This will throw if the key does not exist.
    /// Call Contains(key) first if the key might be missing.
    /// </summary>
    public static AsyncOperationHandle Get(string key)
    {
        return cache[key];
    }

    /// <summary>
    /// Releases every cached Addressables handle and clears the cache.
    /// Use this when leaving a context (e.g., scene/menu) to avoid leaks.
    /// </summary>
    public static void ClearAndRelease()
    {
        foreach (var pair in cache)
        {
            // Release the Addressables handle to decrement reference count.
            Addressables.Release(pair.Value);
        }

        cache.Clear();
    }
}
