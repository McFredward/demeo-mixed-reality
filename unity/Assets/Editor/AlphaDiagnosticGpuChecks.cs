using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

public static class AlphaDiagnosticGpuChecks
{
    private static int checks;
    private static void Check(bool value,string text) { if (!value) throw new Exception(text); checks++; }
    public static void Run()
    {
        var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/AlphaDiagnostics.shader");
        Check(shader!=null && shader.isSupported,"Diagnostic shader must compile on real graphics API");
        var material=new Material(shader) { enableInstancing=true };
        try
        {
            foreach (var format in new[] {GraphicsFormat.R8G8B8A8_UNorm,GraphicsFormat.R8G8B8A8_SRGB})
            foreach (int samples in new[] {1,4})
            foreach (bool conversion in new[] {false,true})
            {
                var desc=new RenderTextureDescriptor(384,256) {graphicsFormat=format,dimension=TextureDimension.Tex2DArray,volumeDepth=2,msaaSamples=samples,depthBufferBits=0};
                Check(SystemInfo.GetRenderTextureSupportedMSAASampleCount(desc)==samples,"Requested MSAA must be supported");
                var target=new RenderTexture(desc);Check(target.Create(),"Create two-eye target");
                try
                {
                    using(var cmd=new CommandBuffer())
                    {
                        for(int eye=0;eye<2;eye++) {cmd.SetRenderTarget(target,0,CubemapFace.Unknown,eye);cmd.ClearRenderTarget(false,true,new Color(eye==0?.1f:.8f,.12f,.15f,1));}
                        cmd.SetRenderTarget(target,0,CubemapFace.Unknown,-1);
                        if(conversion)cmd.EnableShaderKeyword("_LINEAR_TO_SRGB_CONVERSION");else cmd.DisableShaderKeyword("_LINEAR_TO_SRGB_CONVERSION");
                        cmd.EnableShaderKeyword("STEREO_INSTANCING_ON");cmd.SetInstanceMultiplier(2);
                        cmd.DrawProcedural(Matrix4x4.identity,material,0,MeshTopology.Triangles,3);
                        cmd.SetInstanceMultiplier(1);cmd.DisableShaderKeyword("STEREO_INSTANCING_ON");cmd.DisableShaderKeyword("_LINEAR_TO_SRGB_CONVERSION");
                        bool previousSrgb=GL.sRGBWrite;
                        try {GL.sRGBWrite=format==GraphicsFormat.R8G8B8A8_SRGB;Graphics.ExecuteCommandBuffer(cmd);}
                        finally {GL.sRGBWrite=previousSrgb;}
                    }
                    desc.msaaSamples=1;var resolved=new RenderTexture(desc);Check(resolved.Create(),"Create resolved fixture readback");
                    try
                    {
                        if(samples>1)target.ResolveAntiAliasedSurface(resolved);else Graphics.CopyTexture(target,resolved);
                        var request=AsyncGPUReadback.Request(resolved,0);request.WaitForCompletion();
                        Check(!request.hasError && request.layerCount==2,"Read exact two actual GPU slices");
                        for(int eye=0;eye<2;eye++)
                        {
                            var pixels=request.GetData<Color32>(eye);
                            int row=-1;
                            for(int y=0;y<256;y++) {
                                Color p=pixels[y*384+176];
                                if(p.g>.95f && p.r<.01f && p.a>.95f) {row=y;break;}
                            }
                            Check(row>=0,"Opaque green control appears independently in both eyes");
                            for(int cell=0;cell<6;cell++)
                            {
                                Color p=pixels[row*384+112+cell*32];
                                float a=cell==0||cell==4?0:cell==3?.5f:cell==5?.515625f:1;
                                float e=cell==0||cell==2?0:cell==3?.5f:cell==5?.25f*a:.25f;
                                if(conversion)e=Mathf.LinearToGammaSpace(e);
                                if(format==GraphicsFormat.R8G8B8A8_SRGB) e=Mathf.LinearToGammaSpace(e);
                                Check(Mathf.Abs(p.a-a)<.025f,"Known alpha including additive A0 survives shader/MSAA");
                                Check(Mathf.Abs(p.r-e)<.025f && Mathf.Abs(p.b-e)<.025f,"Known RGB contribution survives without second alpha multiply: "+format+" cell="+cell+" conversion="+conversion+" actual="+p+" expected="+e);
                            }
                            Color outside=pixels[128*384+16];
                            Check(outside.a>.99f && (eye==0 ? outside.r<.4f : outside.r>.7f),"Outside strip retains distinct original eye pixels");
                        }
                    }
                    finally {resolved.Release();UnityEngine.Object.DestroyImmediate(resolved);}
                }
                finally {target.Release();UnityEngine.Object.DestroyImmediate(target);}
                Debug.Log("Battlemarked diagnostic GPU case passed: "+format+"/MSAA"+samples+"/conversion="+conversion);
            }
            Debug.Log("Battlemarked diagnostic GPU checks passed: "+checks+"; "+SystemInfo.graphicsDeviceType+"/"+SystemInfo.graphicsDeviceName);
        }
        finally {UnityEngine.Object.DestroyImmediate(material);}
    }
}
