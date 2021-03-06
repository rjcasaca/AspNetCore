// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Browser;
using Microsoft.AspNetCore.Components.Browser.Rendering;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Microsoft.AspNetCore.Components.Server.Circuits
{
    internal class CircuitHost : IAsyncDisposable
    {
        private static readonly AsyncLocal<CircuitHost> _current = new AsyncLocal<CircuitHost>();
        private readonly IServiceScope _scope;
        private readonly IDispatcher _dispatcher;
        private readonly CircuitHandler[] _circuitHandlers;
        private bool _initialized;

        /// <summary>
        /// Gets the current <see cref="Circuit"/>, if any.
        /// </summary>
        public static CircuitHost Current => _current.Value;

        /// <summary>
        /// Sets the current <see cref="Circuits.Circuit"/>.
        /// </summary>
        /// <param name="circuitHost">The <see cref="Circuits.Circuit"/>.</param>
        /// <remarks>
        /// Calling <see cref="SetCurrentCircuitHost(CircuitHost)"/> will store the circuit
        /// and other related values such as the <see cref="IJSRuntime"/> and <see cref="Renderer"/>
        /// in the local execution context. Application code should not need to call this method,
        /// it is primarily used by the Server-Side Components infrastructure.
        /// </remarks>
        public static void SetCurrentCircuitHost(CircuitHost circuitHost)
        {
            _current.Value = circuitHost ?? throw new ArgumentNullException(nameof(circuitHost));

            JSInterop.JSRuntime.SetCurrentJSRuntime(circuitHost.JSRuntime);
            RendererRegistry.SetCurrentRendererRegistry(circuitHost.RendererRegistry);
        }

        public event UnhandledExceptionEventHandler UnhandledException;

        public CircuitHost(
            IServiceScope scope,
            IClientProxy client,
            RendererRegistry rendererRegistry,
            RemoteRenderer renderer,
            IList<ComponentDescriptor> descriptors,
            IDispatcher dispatcher,
            IJSRuntime jsRuntime,
            CircuitHandler[] circuitHandlers)
        {
            _scope = scope ?? throw new ArgumentNullException(nameof(scope));
            _dispatcher = dispatcher;
            Client = client;
            RendererRegistry = rendererRegistry ?? throw new ArgumentNullException(nameof(rendererRegistry));
            Descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
            Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            JSRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
            
            Services = scope.ServiceProvider;

            Circuit = new Circuit(this);
            _circuitHandlers = circuitHandlers;

            Renderer.UnhandledException += Renderer_UnhandledException;
            Renderer.UnhandledSynchronizationException += SynchronizationContext_UnhandledException;
        }

        public string CircuitId { get; } = Guid.NewGuid().ToString();

        public Circuit Circuit { get; }

        public IClientProxy Client { get; set; }

        public IJSRuntime JSRuntime { get; }

        public RemoteRenderer Renderer { get; }

        public RendererRegistry RendererRegistry { get; }

        public IList<ComponentDescriptor> Descriptors { get; }

        public IServiceProvider Services { get; }

        public Task<IEnumerable<string>> PrerenderComponentAsync(Type componentType, ParameterCollection parameters)
        {
            return _dispatcher.InvokeAsync(async () =>
            {
                Renderer.StartPrerender();
                var result = await Renderer.RenderComponentAsync(componentType, parameters);
                return result;
            });
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await Renderer.InvokeAsync(async () =>
            {
                SetCurrentCircuitHost(this);

                for (var i = 0; i < Descriptors.Count; i++)
                {
                    var (componentType, domElementSelector) = Descriptors[i];
                    await Renderer.AddComponentAsync(componentType, domElementSelector);
                }

                for (var i = 0; i < _circuitHandlers.Length; i++)
                {
                    await _circuitHandlers[i].OnCircuitOpenedAsync(Circuit, cancellationToken);
                }

                for (var i = 0; i < _circuitHandlers.Length; i++)
                {
                    await _circuitHandlers[i].OnConnectionUpAsync(Circuit, cancellationToken);
                }
            });

            _initialized = true;
        }

        public async void BeginInvokeDotNetFromJS(string callId, string assemblyName, string methodIdentifier, long dotNetObjectId, string argsJson)
        {
            AssertInitialized();

            try
            {
                await Renderer.Invoke(() =>
                {
                    SetCurrentCircuitHost(this);
                    DotNetDispatcher.BeginInvoke(callId, assemblyName, methodIdentifier, dotNetObjectId, argsJson);
                });
            }
            catch (Exception ex)
            {
                UnhandledException?.Invoke(this, new UnhandledExceptionEventArgs(ex, isTerminating: false));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Renderer.InvokeAsync(async () =>
            {
                for (var i = 0; i < _circuitHandlers.Length; i++)
                {
                    await _circuitHandlers[i].OnConnectionDownAsync(Circuit, default);
                }

                for (var i = 0; i < _circuitHandlers.Length; i++)
                {
                    await _circuitHandlers[i].OnCircuitClosedAsync(Circuit, default);
                }
            });

            _scope.Dispose();
            Renderer.Dispose();
        }

        private void AssertInitialized()
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("Something is calling into the circuit before Initialize() completes");
            }
        }

        private void Renderer_UnhandledException(object sender, Exception e)
        {
            UnhandledException?.Invoke(this, new UnhandledExceptionEventArgs(e, isTerminating: false));
        }

        private void SynchronizationContext_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            UnhandledException?.Invoke(this, e);
        }
    }
}
