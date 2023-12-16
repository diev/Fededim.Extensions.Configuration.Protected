﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.DataProtection;
using System.Diagnostics;
using System.Threading;

namespace Fededim.Extensions.Configuration.Protected
{
    /// <summary>
    /// ProtectedConfigurationProvider is a custom ConfigurationProvider which is actually responsible for real time decryption of configuration values for any existing or even future ConfigurationProvider. It uses the composition pattern.
    /// </summary>
    [DebuggerDisplay("Provider = Protected{Provider}")]
    public class ProtectedConfigurationProvider : IConfigurationProvider, IDisposable
    {
        protected IConfigurationProvider Provider { get; }
        protected ProtectedConfigurationData ProtectedConfigurationData { get; }

        protected ConfigurationReloadToken ReloadToken;

        protected IChangeToken ProviderReloadToken { get; set; }
        protected IDisposable ProviderReloadTokenRegistration { get; set; }


        public ProtectedConfigurationProvider(IConfigurationProvider provider, ProtectedConfigurationData protectedConfigurationData)
        {
            Provider = provider;
            ProtectedConfigurationData = protectedConfigurationData;

            ReloadToken = new ConfigurationReloadToken();

            RegisterReloadCallback();
        }



        /// <summary>
        /// Registers a reload callback which redecrypts all values if the underlying IConfigurationProvider supports it
        /// </summary>
        protected void RegisterReloadCallback()
        {
            ProviderReloadToken = Provider.GetReloadToken();
            ProviderReloadTokenRegistration = ProviderReloadToken?.RegisterChangeCallback((configurationProvider) =>
            {
                var protectedConfigurationProvider = configurationProvider as ProtectedConfigurationProvider;

                protectedConfigurationProvider.DecryptChildKeys();

                RegisterReloadCallback();

                OnReload();

            }, this);
        }



        /// <summary>
        /// Dispatches all the callbacks waiting for the reload event from this configuration provider (and creates a new ReloadToken)
        /// </summary>
        protected void OnReload()
        {
            ConfigurationReloadToken previousToken = Interlocked.Exchange(ref ReloadToken, new ConfigurationReloadToken());
            previousToken.OnReload();
        }




        /// <summary>
        /// Decrypts all values using just IConfigurationBuilder interface methods so it should work on any existing or even future IConfigurationProvider
        /// Note: unluckily there Data dictionary property of ConfigurationProvider is not exposed on the interface IConfigurationProvider, but we can manage to get all keys by using the GetChildKeys methods, look at its implementation https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.Extensions.Configuration/src/ConfigurationProvider.cs#L61-L94
        /// The only drawback of this method is that it returns the child keys of the level of the hierarchy specified by the parentPath parameter (it's at line 71 in MS source code "Segment(kv.Key,0)" )
        /// So you have to use a recursive function to gather all existing keys and also to issue a distinct due to the way the GetChildKeys method has been implemented
        /// </summary>
        /// <param name="parentPath"></param>
        protected void DecryptChildKeys(String parentPath = null)
        {
            foreach (var key in Provider.GetChildKeys(new List<String>(), parentPath).Distinct())
            {
                var fullKey = parentPath != null ? $"{parentPath}:{key}" : key;
                if (Provider.TryGet(fullKey, out var value))
                {
                    if (!String.IsNullOrEmpty(value))
                        Provider.Set(fullKey, ProtectedConfigurationData.ProtectedRegex.Replace(value, me => ProtectedConfigurationData.DataProtector.Unprotect(me.Groups["protectedData"].Value)));
                }
                else DecryptChildKeys(fullKey);
            }
        }



        public IChangeToken GetReloadToken()
        {
            return ReloadToken;
        }




        public void Load()
        {
            Provider.Load();

            if (ProviderReloadToken == null || !ProviderReloadToken.HasChanged)
                DecryptChildKeys();
        }






        public IEnumerable<String> GetChildKeys(IEnumerable<String> earlierKeys, String parentPath)
        {
            return Provider.GetChildKeys(earlierKeys, parentPath);
        }



        public void Set(String key, String value)
        {
            Provider.Set(key, value);
        }



        public bool TryGet(String key, out String value)
        {
            return Provider.TryGet(key, out value);
        }



        public void Dispose()
        {
            ProviderReloadTokenRegistration?.Dispose();
        }
    }
}