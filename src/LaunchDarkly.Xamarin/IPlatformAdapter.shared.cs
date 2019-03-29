﻿using System;
using System.Collections.Generic;
using System.Text;

namespace LaunchDarkly.Xamarin
{
    /// <summary>
    /// Interface for a component that helps <c>LdClient</c> interact with a specific mobile platform.
    /// Currently this is necessary in order to handle features that are not part of the portable
    /// <c>Xamarin.Essentials</c> API; in the future it may be handled automatically when you
    /// create an <c>LdClient</c>.
    /// 
    /// To obtain an instance of this interface, use the implementation of `PlatformComponents.CreatePlatformAdapter`
    /// that is provided by the add-on library for your specific platform (e.g. <c>LaunchDarkly.Xamarin.Android</c>).
    /// Then pass the object to <see cref="ConfigurationExtensions.WithPlatformAdapter(Configuration, IPlatformAdapter)"/>
    /// when you are building your client configuration.
    /// 
    /// Application code should not call any methods of this interface directly; they are used internally
    /// by <c>LdClient</c>.
    /// </summary>
    public interface IPlatformAdapter : IDisposable
    {
        /// <summary>
        /// Tells the <c>IPlatformAdapter</c> to start monitoring the foreground/background state of
        /// the application, and provides a callback object for it to use when the state changes.
        /// </summary>
        /// <param name="backgroundingState">An implementation of <c>IBackgroundingState</c> provided by the client</param>
        void EnableBackgrounding(IBackgroundingState backgroundingState);
    }

    internal class NullPlatformAdapter : IPlatformAdapter
    {
        public void EnableBackgrounding(IBackgroundingState backgroundingState) { }

        public void Dispose() { }
    }
}