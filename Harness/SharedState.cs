using System.Collections.Generic;

namespace BLTDeploymentCrashGuard
{
    /// <summary>Harness-owned cross-reload state store (see ISharedState).</summary>
    public sealed class SharedState : ISharedState
    {
        private readonly Dictionary<string, object> _store = new Dictionary<string, object>();
        private readonly object _sync = new object();

        public T Get<T>(string key)
        {
            lock (_sync)
            {
                object v;
                if (_store.TryGetValue(key, out v) && v is T)
                {
                    return (T)v;
                }
                return default(T);
            }
        }

        public object GetObject(string key)
        {
            lock (_sync)
            {
                object v;
                return _store.TryGetValue(key, out v) ? v : null;
            }
        }

        public void Set(string key, object value)
        {
            lock (_sync)
            {
                _store[key] = value;
            }
        }

        public bool Has(string key)
        {
            lock (_sync)
            {
                return _store.ContainsKey(key);
            }
        }
    }
}
