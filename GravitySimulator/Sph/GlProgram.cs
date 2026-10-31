using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using OpenTK.Graphics.OpenGL4;

namespace GravitySimulator.Sph
{
    /// <summary>
    /// A GLSL program built from shaders embedded under Sph/Shaders. Compute shaders get
    /// <c>#version 430 core</c> and common.glsl prepended. Every compile and link is checked and a
    /// failure throws with the shader name and the driver's info log.
    /// </summary>
    internal sealed class GlProgram : IDisposable
    {
        private const string ResourcePrefix = "GravitySimulator.Sph.Shaders.";
        private readonly Dictionary<string, int> _locations = new Dictionary<string, int>();

        public int Handle { get; private set; }
        public string Name { get; }

        private GlProgram(int handle, string name)
        {
            Handle = handle;
            Name = name;
        }

        public static string LoadSource(string file)
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + file)
                ?? throw new InvalidOperationException("Shader resource not found: " + ResourcePrefix + file);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public static GlProgram Compute(string file)
        {
            string source = "#version 430 core\n" + LoadSource("common.glsl") + "\n#line 1\n" + LoadSource(file);
            int shader = CompileShader(ShaderType.ComputeShader, source, file);
            return Link(file, shader);
        }

        public static GlProgram Graphics(string vertexFile, string fragmentFile)
        {
            int vs = CompileShader(ShaderType.VertexShader, LoadSource(vertexFile), vertexFile);
            int fs;
            try { fs = CompileShader(ShaderType.FragmentShader, LoadSource(fragmentFile), fragmentFile); }
            catch { GL.DeleteShader(vs); throw; }
            return Link(vertexFile + " + " + fragmentFile, vs, fs);
        }

        /// <summary>Builds a program from source strings (used to reuse Planet formation's point shader).</summary>
        public static GlProgram FromSource(string name, string vertexSource, string fragmentSource)
        {
            int vs = CompileShader(ShaderType.VertexShader, vertexSource, name + " (vertex)");
            int fs;
            try { fs = CompileShader(ShaderType.FragmentShader, fragmentSource, name + " (fragment)"); }
            catch { GL.DeleteShader(vs); throw; }
            return Link(name, vs, fs);
        }

        private static int CompileShader(ShaderType type, string source, string name)
        {
            int s = GL.CreateShader(type);
            GL.ShaderSource(s, source);
            GL.CompileShader(s);
            GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0)
            {
                string log = GL.GetShaderInfoLog(s);
                GL.DeleteShader(s);
                throw new InvalidOperationException($"Shader {name} ({type}) failed to compile:\n{log}");
            }
            return s;
        }

        private static GlProgram Link(string name, params int[] shaders)
        {
            int program = GL.CreateProgram();
            foreach (int s in shaders) GL.AttachShader(program, s);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int ok);
            foreach (int s in shaders)
            {
                GL.DetachShader(program, s);
                GL.DeleteShader(s);
            }
            if (ok == 0)
            {
                string log = GL.GetProgramInfoLog(program);
                GL.DeleteProgram(program);
                throw new InvalidOperationException($"Shader program {name} failed to link:\n{log}");
            }
            return new GlProgram(program, name);
        }

        public int Location(string name)
        {
            if (!_locations.TryGetValue(name, out int loc))
            {
                loc = GL.GetUniformLocation(Handle, name);
                _locations[name] = loc;
            }
            return loc;
        }

        public void Set(string name, float value) { int l = Location(name); if (l >= 0) GL.ProgramUniform1(Handle, l, value); }
        public void Set(string name, int value) { int l = Location(name); if (l >= 0) GL.ProgramUniform1(Handle, l, value); }
        public void Set(string name, uint value) { int l = Location(name); if (l >= 0) GL.ProgramUniform1((uint)Handle, l, value); } // (int, int, uint) would bind to the double overload
        public void Set(string name, bool value) => Set(name, value ? 1 : 0);
        public void Set(string name, Vector3 v) { int l = Location(name); if (l >= 0) GL.ProgramUniform3(Handle, l, v.X, v.Y, v.Z); }
        public void SetArray4(string name, float[] values, int count) { int l = Location(name); if (l >= 0 && count > 0) GL.ProgramUniform4(Handle, l, count, values); }
        public void SetArray1(string name, float[] values, int count) { int l = Location(name); if (l >= 0 && count > 0) GL.ProgramUniform1(Handle, l, count, values); }

        public void SetMatrix(string name, Matrix4x4 m)
        {
            int l = Location(name);
            if (l < 0) return;
            float[] a =
            {
                m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
            };
            GL.ProgramUniformMatrix4(Handle, l, 1, false, a);
        }

        /// <summary>Runs this compute program over <paramref name="invocations"/> threads in groups of <paramref name="groupSize"/>.</summary>
        public void Dispatch(int invocations, int groupSize)
        {
            if (invocations <= 0) return;
            GL.UseProgram(Handle);
            GL.DispatchCompute((invocations + groupSize - 1) / groupSize, 1, 1);
        }

        public void Dispose()
        {
            if (Handle != 0) GL.DeleteProgram(Handle);
            Handle = 0;
        }
    }
}
