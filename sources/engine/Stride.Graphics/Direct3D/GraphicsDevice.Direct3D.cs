// Copyright (c) Stride contributors (https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#if STRIDE_GRAPHICS_API_DIRECT3D11
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.Direct3D11.Debug;
using static Vortice.Direct3D11.D3D11;

namespace Stride.Graphics
{
    public partial class GraphicsDevice
    {
        internal readonly int ConstantBufferDataPlacementAlignment = 16;

        private const GraphicsPlatform GraphicPlatform = GraphicsPlatform.Direct3D11;

        private bool simulateReset = false;
        private string rendererName;

        private ID3D11Device nativeDevice;
        private ID3D11DeviceContext nativeDeviceContext;
        private readonly Queue<ID3D11Query> disjointQueries = new Queue<ID3D11Query>(4);
        private readonly Stack<ID3D11Query> currentDisjointQueries = new Stack<ID3D11Query>(2);

        internal GraphicsProfile RequestedProfile;

        private Vortice.Direct3D11.DeviceCreationFlags creationFlags;

        /// <summary>
        /// The tick frquency of timestamp queries in Hertz.
        /// </summary>
        public ulong TimestampFrequency { get; private set; }

        /// <summary>
        ///     Gets the status of this device.
        /// </summary>
        /// <value>The graphics device status.</value>
        public GraphicsDeviceStatus GraphicsDeviceStatus
        {
            get
            {
                if (simulateReset)
                {
                    simulateReset = false;
                    return GraphicsDeviceStatus.Reset;
                }

                var result = NativeDevice.DeviceRemovedReason;
                if (result == Vortice.DXGI.ResultCode.DeviceRemoved)
                {
                    return GraphicsDeviceStatus.Removed;
                }

                if (result == Vortice.DXGI.ResultCode.DeviceReset)
                {
                    return GraphicsDeviceStatus.Reset;
                }

                if (result == Vortice.DXGI.ResultCode.DeviceHung)
                {
                    return GraphicsDeviceStatus.Hung;
                }

                if (result == Vortice.DXGI.ResultCode.DriverInternalError)
                {
                    return GraphicsDeviceStatus.InternalError;
                }

                if (result == Vortice.DXGI.ResultCode.InvalidCall)
                {
                    return GraphicsDeviceStatus.InvalidCall;
                }

                if (result.Code < 0)
                {
                    return GraphicsDeviceStatus.Reset;
                }

                return GraphicsDeviceStatus.Normal;
            }
        }

        /// <summary>
        ///     Gets the native device.
        /// </summary>
        /// <value>The native device.</value>
        internal ID3D11Device NativeDevice
        {
            get
            {
                return nativeDevice;
            }
        }

        /// <summary>
        /// Gets the native device context.
        /// </summary>
        /// <value>The native device context.</value>
        internal ID3D11DeviceContext NativeDeviceContext
        {
            get
            {
                return nativeDeviceContext;
            }
        }

        /// <summary>
        ///     Marks context as active on the current thread.
        /// </summary>
        public void Begin()
        {
            FrameTriangleCount = 0;
            FrameDrawCalls = 0;

            ID3D11Query currentDisjointQuery;

            // Try to read back the oldest disjoint query and reuse it. If not ready, create a new one.
            if (disjointQueries.Count > 0 && NativeDeviceContext.GetData(disjointQueries.Peek(), out QueryDataTimestampDisjoint result))
            {
                TimestampFrequency = result.Frequency;
                currentDisjointQuery = disjointQueries.Dequeue();
            }
            else
            {
                var disjointQueryDiscription = new QueryDescription(Vortice.Direct3D11.QueryType.Timestamp);
                currentDisjointQuery = NativeDevice.CreateQuery(disjointQueryDiscription);
            }

            currentDisjointQueries.Push(currentDisjointQuery);
            NativeDeviceContext.Begin(currentDisjointQuery);
        }

        /// <summary>
        /// Enables profiling.
        /// </summary>
        /// <param name="enabledFlag">if set to <c>true</c> [enabled flag].</param>
        public void EnableProfile(bool enabledFlag)
        {
        }

        /// <summary>
        ///     Unmarks context as active on the current thread.
        /// </summary>
        public void End()
        {
            // If this fails, it means Begin()/End() don't match, something is very wrong
            var currentDisjointQuery = currentDisjointQueries.Pop();
            NativeDeviceContext.End(currentDisjointQuery);
            disjointQueries.Enqueue(currentDisjointQuery);
        }

        /// <summary>
        /// Executes a deferred command list.
        /// </summary>
        /// <param name="commandList">The deferred command list.</param>
        public void ExecuteCommandList(CompiledCommandList commandList)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Executes multiple deferred command lists.
        /// </summary>
        /// <param name="commandLists">The deferred command lists.</param>
        public void ExecuteCommandLists(int count, CompiledCommandList[] commandLists)
        {
            throw new NotImplementedException();
        }

        public void SimulateReset()
        {
            simulateReset = true;
        }

        private void InitializePostFeatures()
        {
            // Create the main command list
            InternalMainCommandList = new CommandList(this);
        }

        private string GetRendererName()
        {
            return rendererName;
        }

        /// <summary>
        ///     Initializes the specified device.
        /// </summary>
        /// <param name="graphicsProfiles">The graphics profiles.</param>
        /// <param name="deviceCreationFlags">The device creation flags.</param>
        /// <param name="windowHandle">The window handle.</param>
        private void InitializePlatformDevice(GraphicsProfile[] graphicsProfiles, DeviceCreationFlags deviceCreationFlags, object windowHandle)
        {
            if (nativeDevice != null)
            {
                // Destroy previous device
                ReleaseDevice();
            }

            rendererName = Adapter.NativeAdapter.Description.Description;

            // Profiling is supported through pix markers
            IsProfilingSupported = true;

            // Map GraphicsProfile to D3D11 FeatureLevel
            creationFlags = (Vortice.Direct3D11.DeviceCreationFlags)deviceCreationFlags;

            // Create Device D3D11 with feature Level based on profile
            for (int index = 0; index < graphicsProfiles.Length; index++)
            {
                var graphicsProfile = graphicsProfiles[index];
                try
                {
                    // D3D12 supports only feature level 11+
                    var level = graphicsProfile.ToFeatureLevel();

                    // INTEL workaround: it seems Intel driver doesn't support properly feature level 9.x. Fallback to 10.
                    if (Adapter.VendorId == 0x8086)
                    {
                        if (level < Vortice.Direct3D.FeatureLevel.Level_10_0)
                            level = Vortice.Direct3D.FeatureLevel.Level_10_0;
                    }

#if STRIDE_PLATFORM_WINDOWS_DESKTOP
                    // If RenderDoc is loaded, force level 11+
                    if (GetModuleHandle("renderdoc.dll") != IntPtr.Zero)
                    {
                        if (level < Vortice.Direct3D.FeatureLevel.Level_11_0)
                            level = Vortice.Direct3D.FeatureLevel.Level_11_0;
                    }
#endif

                    D3D11CreateDevice(Adapter.NativeAdapter, creationFlags, level, out nativeDevice).CheckError();

                    // INTEL workaround: force ShaderProfile to be 10+ as well
                    if (Adapter.VendorId == 0x8086)
                    {
                        if (graphicsProfile < GraphicsProfile.Level_10_0 && (!ShaderProfile.HasValue || ShaderProfile.Value < GraphicsProfile.Level_10_0))
                            ShaderProfile = GraphicsProfile.Level_10_0;
                    }

                    RequestedProfile = graphicsProfile;
                    break;
                }
                catch (Exception)
                {
                    if (index == graphicsProfiles.Length - 1)
                        throw;
                }
            }

            nativeDeviceContext = nativeDevice.ImmediateContext;
            // We keep one reference so that it doesn't disappear with InternalMainCommandList
            ((IUnknown)nativeDeviceContext).AddRef();
            if (IsDebugMode)
            {
                GraphicsResourceBase.SetDebugName(this, nativeDeviceContext, "ImmediateContext");
            }
        }

        private void AdjustDefaultPipelineStateDescription(ref PipelineStateDescription pipelineStateDescription)
        {
            // On D3D, default state is Less instead of our LessEqual
            // Let's update default pipeline state so that it correspond to D3D state after a "ClearState()"
            pipelineStateDescription.DepthStencilState.DepthBufferFunction = CompareFunction.Less;
        }

        protected void DestroyPlatformDevice()
        {
            ReleaseDevice();
        }

        private void ReleaseDevice()
        {
            foreach (var query in disjointQueries)
            {
                query.Dispose();
            }
            disjointQueries.Clear();

            // Display D3D11 ref counting info
            NativeDevice.ImmediateContext.ClearState();
            NativeDevice.ImmediateContext.Flush();

            if (IsDebugMode)
            {
                var debugDevice = NativeDevice.QueryInterfaceOrNull<ID3D11Debug>();
                if (debugDevice != null)
                {
                    debugDevice.ReportLiveDeviceObjects(ReportLiveDeviceObjectFlags.Detail);
                    debugDevice.Dispose();
                }
            }

            nativeDevice.Dispose();
            nativeDevice = null;
        }

        internal void OnDestroyed()
        {
        }

        internal void TagResource(GraphicsResourceLink resourceLink)
        {
            if (resourceLink.Resource is GraphicsResource resource)
                resource.DiscardNextMap = true;
        }

#if STRIDE_PLATFORM_WINDOWS_DESKTOP
        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandle", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
#endif
    }
}
#endif
