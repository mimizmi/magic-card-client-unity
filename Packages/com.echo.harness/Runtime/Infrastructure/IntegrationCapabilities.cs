using System;
using UnityEngine.AddressableAssets;

namespace Echo.Harness.Infrastructure
{
    public static class IntegrationCapabilities
    {
        public static bool AddressablesPackageAvailable =>
            typeof(Addressables) != null;

        public static bool XluaPackageAvailable =>
            Type.GetType("XLua.LuaEnv, XLua", false) != null;
    }
}
