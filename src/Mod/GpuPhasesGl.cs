using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTK.Graphics.OpenGL;
using SparkVS.Core;
using Vintagestory.API.Client;

namespace SparkVS;

/// <summary>GL timestamp queries through the same OpenTK.Graphics.OpenGL class the game calls. Render thread only.</summary>
sealed class OpenGlTimer : IGpuTimer
{
    public void GenQueries(int[] ids) => GL.GenQueries(ids.Length, ids);
    public void Stamp(int id) => GL.QueryCounter(id, QueryCounterTarget.Timestamp);

    public bool IsAvailable(int id)
    {
        GL.GetQueryObject(id, GetQueryObjectParam.QueryResultAvailable, out int available);
        return available != 0;
    }

    public long Result(int id)
    {
        GL.GetQueryObject(id, GetQueryObjectParam.QueryResult, out long value);
        return value;
    }

    /// <summary>Checks the context can do timestamp queries. Returns null and a reason when it cannot. Render thread only.</summary>
    public static GpuInfo? Probe(out string reason)
    {
        reason = "";
        var info = new GpuInfo();
        try
        {
            info.Vendor = GL.GetString(StringName.Vendor) ?? "";
            info.Renderer = GL.GetString(StringName.Renderer) ?? "";
            while (GL.GetError() != ErrorCode.NoError) { }   // errors left by earlier code, so only ours are reported
            GL.GetInteger(GetPName.MajorVersion, out int major);
            GL.GetInteger(GetPName.MinorVersion, out int minor);
            bool core = major * 10 + minor >= 33;   // ARB_timer_query is core from 3.3
            if (!core && !HasExtension("GL_ARB_timer_query"))
            {
                reason = $"GL {major}.{minor} without GL_ARB_timer_query";
                return null;
            }
            GL.GetQuery(QueryTarget.Timestamp, GetQueryParam.QueryCounterBits, out int bits);
            info.TimerBits = bits;
            var err = GL.GetError();
            if (err != ErrorCode.NoError) { reason = "GL error " + err + " while probing timer queries"; return null; }
            if (bits <= 0) { reason = "GL_QUERY_COUNTER_BITS is 0 for GL_TIMESTAMP"; return null; }
            return info;
        }
        catch (Exception ex)
        {
            reason = "timer query probe failed: " + ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    static bool HasExtension(string name)
    {
        GL.GetInteger(GetPName.NumExtensions, out int n);
        for (int i = 0; i < n; i++)
            if (GL.GetString(StringNameIndexed.Extensions, i) == name) return true;
        return false;
    }
}

/// <summary>
/// One of these per boundary. The start markers sit at RenderOrder -1e9 and the end markers at +1e9 of their stage, so every other renderer of
/// the stage runs between them. The Before start marker also opens the frame in the recorder. Does nothing unless a capture is running.
/// </summary>
sealed class GpuPhaseMarker : IRenderer
{
    public const double Early = -1e9, Late = 1e9;
    readonly GpuPhaseRecorder recorder;
    readonly int boundary;

    public EnumRenderStage Stage { get; }
    public double RenderOrder { get; }
    public int RenderRange => 0;

    public GpuPhaseMarker(GpuPhaseRecorder recorder, EnumRenderStage stage, int boundary, bool start)
    {
        this.recorder = recorder;
        this.boundary = boundary;
        Stage = stage;
        RenderOrder = start ? Early : Late;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!recorder.Active) return;
        if (boundary == GpuBoundary.FrameStart) recorder.BeginFrame(Stopwatch.GetTimestamp());
        else recorder.Mark(boundary);
    }

    public void Dispose() { }

    /// <summary>The stage and boundary table. Order and meaning are in gpuphases-notes.md.</summary>
    public static List<GpuPhaseMarker> CreateAll(GpuPhaseRecorder r) => new()
    {
        new(r, EnumRenderStage.Before, GpuBoundary.FrameStart, true),
        new(r, EnumRenderStage.ShadowFar, GpuBoundary.ShadowStart, true),
        new(r, EnumRenderStage.ShadowFarDone, GpuBoundary.ShadowFarDone, false),
        new(r, EnumRenderStage.ShadowNearDone, GpuBoundary.ShadowNearDone, false),
        new(r, EnumRenderStage.Opaque, GpuBoundary.OpaqueStart, true),
        new(r, EnumRenderStage.Opaque, GpuBoundary.OpaqueEnd, false),
        new(r, EnumRenderStage.AfterOIT, GpuBoundary.AfterOitEnd, false),
        new(r, EnumRenderStage.AfterPostProcessing, GpuBoundary.AfterPostEnd, false),
        new(r, EnumRenderStage.AfterBlit, GpuBoundary.AfterBlitEnd, false),
        new(r, EnumRenderStage.Done, GpuBoundary.DoneEnd, false),
    };
}
