using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

class RadianceCascadePass : CustomPass
{
    public struct Params
    {
        public uint totalRays;
        public int probeRadius;
        public int probeRayCount;
        public int level;
        public int levelCount;
        public int width;
        public int height;
        public int maxLevel0Rays;
        public int intervalStartRadius;
        public int intervalEndRadius;
        public int branchingFactor;
    }

    public ComputeShader shader;

    Params[] _params;
    Vector4[] probes;

    ComputeBuffer paramsBuffer;
    ComputeBuffer probesBuffer;

    int ScreenWidth = 2048;
    int ScreenHeight = 2048;
    int probeDiameter = 2;
    int levelCount = 5;
    int probeRayCount = 4;
    int branchingFactor = 2;
    int intervalRadius = 6;
    int maxProbeRays = 32;
    int minProbeDiameter = 2;
    int maxProbeCount = 0;
    int maxLevel0Rays = 0;

    private RenderTextureDescriptor textureDescriptor;
    private RTHandle textureHandle;
    private RenderTextureDescriptor inftextureDescriptor;
    private RTHandle inftextureHandle;
    private Material mat;
    private Shader blitShader;

    protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
    {
        textureDescriptor = new RenderTextureDescriptor(ScreenWidth,
            ScreenHeight, RenderTextureFormat.ARGBFloat, 0);
        textureDescriptor.useMipMap = true;
        textureDescriptor.autoGenerateMips = true;
        textureDescriptor.enableRandomWrite = true;
        inftextureDescriptor = new RenderTextureDescriptor(ScreenWidth,
            ScreenHeight, RenderTextureFormat.ARGBFloat, 0);
        inftextureDescriptor.enableRandomWrite = true;

        textureHandle = RTHandles.Alloc(textureDescriptor);
        inftextureHandle = RTHandles.Alloc(inftextureDescriptor);

        blitShader = Shader.Find("FullScreen/Blit");
        mat = CoreUtils.CreateEngineMaterial(blitShader);

        this.maxProbeCount = (int)((ScreenWidth / minProbeDiameter) * (ScreenHeight / minProbeDiameter));
        this.maxLevel0Rays = maxProbeRays * maxProbeCount;
        int probeBufferSize = maxProbeRays * maxProbeCount * 2;
        probes = new Vector4[probeBufferSize];

        if (paramsBuffer == null)
            paramsBuffer = new ComputeBuffer(levelCount + 1, (sizeof(uint) * 1) + (sizeof(int) * 10));

        if (probesBuffer == null)
            probesBuffer = new ComputeBuffer(probes.Length, sizeof(float) * 4);

        generateParams();

        probesBuffer.SetData(probes);
        shader.SetBuffer(0, "_probes", probesBuffer);
        shader.SetBuffer(1, "_probes", probesBuffer);
        paramsBuffer.SetData(_params);
        shader.SetBuffer(0, "_params", paramsBuffer);
        shader.SetBuffer(1, "_params", paramsBuffer);
        shader.SetTexture(0, "Result", textureHandle.rt);
        shader.SetTexture(1, "Result2", inftextureHandle.rt);
    }

    protected override void Execute(CustomPassContext ctx)
    {
        if (ctx.hdCamera.camera.cameraType == CameraType.SceneView)
            return;
        CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, textureHandle);
        RenderFrame();
        ctx.propertyBlock.SetTexture("_texture", inftextureHandle);
        CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
        CoreUtils.DrawFullScreen(ctx.cmd, mat, ctx.propertyBlock, shaderPassId: 0);
    }

    protected override void Cleanup()
    {
        if (mat != null) CoreUtils.Destroy(mat);
        if (textureHandle != null) textureHandle.Release();
        if (inftextureHandle != null) inftextureHandle.Release();
    }

    private void RenderFrame()
    {
        for (int level = levelCount; level >= 0; level--)
        {
            CalculateCascades(level);
        }
        fillInfluenceTexture();
    }

    private void generateParams()
    {
        for (int level = levelCount; level >= 0; level--)
        {
            float currenProbeDiameter = probeDiameter << level;
            int currentProbeRayCount = probeRayCount << (level * branchingFactor);
            int intervalStartRadius = level == 0 ? 0 : intervalRadius << ((level - 1) * branchingFactor);
            int intervalEndRadius = intervalRadius << (level * branchingFactor);
            uint totalRays = Convert.ToUInt32(((ScreenWidth / currenProbeDiameter) * (ScreenHeight / currenProbeDiameter) * currentProbeRayCount));
            fillParams(totalRays, currenProbeDiameter * 0.5f, currentProbeRayCount, level, levelCount, ScreenWidth, ScreenHeight, maxLevel0Rays, intervalStartRadius, intervalEndRadius, branchingFactor);
        }
    }

    private void CalculateCascades(int paramsIndex)
    {
        float probeDiameter = _params[paramsIndex].probeRadius * 2.0f;
        int totalRays = (int)((ScreenWidth / probeDiameter) * (ScreenHeight / probeDiameter) * _params[paramsIndex].probeRayCount);
        int totalWorkGroups = (int)Math.Floor((float)(totalRays / 256 + 1));
        int workgroupsX = totalWorkGroups;
        int workgroupsY = 1;
        shader.SetInt("cascadeLevel", paramsIndex);
        if (workgroupsX > SystemInfo.maxComputeWorkGroupSizeX)
        {
            workgroupsX = SystemInfo.maxComputeWorkGroupSizeX;
            workgroupsY = (int)Math.Floor((float)(totalWorkGroups / SystemInfo.maxComputeWorkGroupSizeX + 1));
        }
        shader.Dispatch(0, workgroupsX, workgroupsY, 1);
    }

    private void fillParams(uint totalRays, float probeRadius, int probeRayCount, int level, int levelCount, int width, int height, int maxLevel0Rays, int intervalStartRadius, int intervalEndRadius, int branchingFactor)
    {
        if (_params == null)
            _params = new Params[levelCount + 1];
        _params[level].totalRays = totalRays;
        _params[level].probeRadius = (int)probeRadius;
        _params[level].probeRayCount = probeRayCount;
        _params[level].level = level;
        _params[level].levelCount = levelCount;
        _params[level].width = width;
        _params[level].height = height;
        _params[level].maxLevel0Rays = maxLevel0Rays;
        _params[level].intervalStartRadius = intervalStartRadius;
        _params[level].intervalEndRadius = intervalEndRadius;
        _params[level].branchingFactor = branchingFactor;
    }

    private void fillInfluenceTexture()
    {
        shader.Dispatch(1, (int)Mathf.Floor(ScreenWidth / 16 + 1), (int)Mathf.Floor(ScreenHeight / 16 + 1), 1);
    }
}