using System;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace GravitySimulator.Sph
{
    /// <summary>
    /// A hidden OpenGL 4.3 core context (an invisible GLFW window) made current on the calling thread,
    /// for headless GPU tests and benchmarks.
    /// </summary>
    public sealed class HeadlessGlContext : IDisposable
    {
        private NativeWindow _window;

        private HeadlessGlContext(NativeWindow window)
        {
            _window = window;
            Renderer = GL.GetString(StringName.Renderer) ?? "";
            Version = GL.GetString(StringName.Version) ?? "";
        }

        public string Renderer { get; }
        public string Version { get; }

        /// <summary>Creates the context, or returns null with the reason in <paramref name="error"/>.</summary>
        public static HeadlessGlContext TryCreate(out string error)
        {
            error = null;
            try
            {
                GLFWProvider.CheckForMainThread = false; // test runners call from worker threads
                var settings = new NativeWindowSettings
                {
                    API = ContextAPI.OpenGL,
                    APIVersion = new Version(4, 3),
                    Profile = ContextProfile.Core,
                    Flags = ContextFlags.ForwardCompatible | ContextFlags.Offscreen,
                    StartVisible = false,
                    StartFocused = false,
                    ClientSize = new OpenTK.Mathematics.Vector2i(64, 64),
                    Title = "SPH headless",
                };
                var window = new NativeWindow(settings);
                window.MakeCurrent();
                return new HeadlessGlContext(window);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        public void Dispose()
        {
            _window?.Dispose();
            _window = null;
        }
    }
}
