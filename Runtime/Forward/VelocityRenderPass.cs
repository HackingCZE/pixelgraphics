using System.Collections.Generic;
using Aarthificial.PixelGraphics.Common;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Aarthificial.PixelGraphics.Forward
{
    public class VelocityRenderPass : ScriptableRenderPass
    {
        private readonly List<ShaderTagId> _shaderTagIdList = new List<ShaderTagId>();
        private readonly ProfilingSampler _profilingSampler;

        // RTHandle místo RenderTargetHandle
        private RTHandle _temporaryVelocityTarget;
        private RTHandle _velocityTarget;

        private readonly Material _emitterMaterial;
        private readonly Material _blitMaterial;

        private VelocityPassSettings _passSettings;
        private SimulationSettings _simulationSettings;
        private FilteringSettings _filteringSettings;
        private Vector2 _previousPosition;
        private RTHandle _cameraColorTarget;

        public VelocityRenderPass(Material emitterMaterial, Material blitMaterial)
        {
            _emitterMaterial = emitterMaterial;
            _blitMaterial = blitMaterial;

            // RTHandle se neinituje přes .Init, alokuje se až podle velikosti rendertargetu.

            _shaderTagIdList.Add(new ShaderTagId("SRPDefaultUnlit"));
            _shaderTagIdList.Add(new ShaderTagId("UniversalForward"));
            _shaderTagIdList.Add(new ShaderTagId("Universal2D"));
            _shaderTagIdList.Add(new ShaderTagId("UniversalForwardOnly"));
            _shaderTagIdList.Add(new ShaderTagId("LightweightForward"));

            _filteringSettings = new FilteringSettings(RenderQueueRange.transparent);
            _profilingSampler = new ProfilingSampler(nameof(VelocityRenderPass));

            renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
        }

        public void Setup(
            VelocityPassSettings passSettings,
            SimulationSettings simulationSettings
        )
        {
            _passSettings = passSettings;
            _simulationSettings = simulationSettings;
        }
        
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            _cameraColorTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;

            // To zajistí, že colorAttachmentHandle už není null
            ConfigureTarget(_cameraColorTarget);
        }


        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cmd = CommandBufferPool.Get();
            cmd.Clear();

            using (new ProfilingScope(cmd, _profilingSampler))
            {
                ref var cameraData = ref renderingData.cameraData;

                int textureWidth = Mathf.FloorToInt(cameraData.camera.pixelWidth * _passSettings.textureScale);
                int textureHeight = Mathf.FloorToInt(cameraData.camera.pixelHeight * _passSettings.textureScale);

                float height = 2 * cameraData.camera.orthographicSize * _passSettings.pixelsPerUnit;
                float width = height * cameraData.camera.aspect;

                var cameraPosition = (Vector2)cameraData.GetViewMatrix().GetColumn(3);
                var delta = cameraPosition - _previousPosition;
                var screenDelta = cameraData.GetProjectionMatrix() * cameraData.GetViewMatrix() * delta;
                _previousPosition = cameraPosition;

                // ─────────────────────────────────────────────
                // RTHandle alokace místo GetTemporaryRT
                // ─────────────────────────────────────────────
                var desc = renderingData.cameraData.cameraTargetDescriptor;
                desc.width = textureWidth;
                desc.height = textureHeight;
                desc.depthBufferBits = 0;
                desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;

                RenderingUtils.ReAllocateIfNeeded(
                    ref _temporaryVelocityTarget,
                    desc,
                    FilterMode.Bilinear,
                    TextureWrapMode.Clamp,
                    name: "_PG_TemporaryVelocityTextureTarget"
                );

                RenderingUtils.ReAllocateIfNeeded(
                    ref _velocityTarget,
                    desc,
                    FilterMode.Bilinear,
                    TextureWrapMode.Clamp,
                    name: "_VelocityTarget"
                );

                // ─────────────────────────────────────────────
                // Globální parametry
                // ─────────────────────────────────────────────
                cmd.SetGlobalVector(ShaderIds.CameraPositionDelta, screenDelta / 2);
                cmd.SetGlobalTexture(ShaderIds.VelocityTexture, _velocityTarget);
                cmd.SetGlobalTexture(ShaderIds.PreviousVelocityTexture, _temporaryVelocityTarget);
                cmd.SetGlobalVector(ShaderIds.VelocitySimulationParams, _simulationSettings.Value);
                cmd.SetGlobalVector(
                    ShaderIds.PixelScreenParams,
                    new Vector4(
                        width,
                        height,
                        _passSettings.pixelsPerUnit,
                        1f / _passSettings.pixelsPerUnit
                    )
                );

                // ─────────────────────────────────────────────
                // Fullscreen pass do velocity targetu
                // ─────────────────────────────────────────────
                CoreUtils.SetRenderTarget(cmd, _velocityTarget);
                cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                cmd.SetViewport(new Rect(0, 0, textureWidth, textureHeight));
                cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, _blitMaterial, 0, 0);
                cmd.SetViewProjectionMatrices(cameraData.GetViewMatrix(), cameraData.GetProjectionMatrix());
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                // ─────────────────────────────────────────────
                // Render emitterů podle layer / rendering layer
                // ─────────────────────────────────────────────
                if (!cameraData.isPreviewCamera && !cameraData.isSceneViewCamera)
                {
                    var drawingSettings = CreateDrawingSettings(
                        _shaderTagIdList,
                        ref renderingData,
                        SortingCriteria.CommonTransparent
                    );

                    if (_passSettings.layerMask != 0)
                    {
                        _filteringSettings.layerMask = _passSettings.layerMask;
                        _filteringSettings.renderingLayerMask = uint.MaxValue;
                        drawingSettings.overrideMaterial = null;
                        context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref _filteringSettings);
                    }

                    if (_passSettings.renderingLayerMask != 0)
                    {
                        _filteringSettings.layerMask = -1;
                        _filteringSettings.renderingLayerMask = _passSettings.renderingLayerMask;
                        drawingSettings.overrideMaterial = _emitterMaterial;
                        context.DrawRenderers(renderingData.cullResults, ref drawingSettings, ref _filteringSettings);
                    }
                }

                // ─────────────────────────────────────────────
// TODO Implement proper double buffering
// kopíruje velocity do temporary bufferu
// ─────────────────────────────────────────────
                cmd.Blit(_velocityTarget, _temporaryVelocityTarget);

#if UNITY_EDITOR
                if (_passSettings.preview)
                    cmd.Blit(_velocityTarget, _cameraColorTarget);
#endif

                CoreUtils.SetRenderTarget(cmd, _cameraColorTarget);

            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            // Uvolnění RTHandle (místo ReleaseTemporaryRT)
            if (_temporaryVelocityTarget != null)
            {
                _temporaryVelocityTarget.Release();
                _temporaryVelocityTarget = null;
            }

            if (_velocityTarget != null)
            {
                _velocityTarget.Release();
                _velocityTarget = null;
            }
        }
    }
}
