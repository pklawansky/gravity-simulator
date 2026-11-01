using System;
using System.Numerics;
using System.Threading.Tasks;
using OpenTK.Graphics.OpenGL4;

namespace GravitySimulator.Sph
{
    /// <summary>
    /// A <see cref="FlatTree"/> on the GPU: three vec4 storage buffers in the layout the tree walks read
    /// (gravity.comp, planet_gravity.comp): A = centre of mass + mass, B = cube centre + half size,
    /// C = (skip, body) as int bits + softening². <see cref="Pack"/> is CPU-only; <see cref="Upload"/>
    /// needs the owning GL context current. Shared by the SPH solver and Planet formation's GPU gravity.
    /// </summary>
    internal sealed class GpuFlatTree : IDisposable
    {
        private const int ParallelPackThreshold = 32_768;

        private float[] _a = Array.Empty<float>(), _b = Array.Empty<float>(), _c = Array.Empty<float>();
        private int _capacity;

        public int NodeA { get; private set; }
        public int NodeB { get; private set; }
        public int NodeC { get; private set; }
        /// <summary>Nodes in the last <see cref="Pack"/> (and, after <see cref="Upload"/>, on the GPU).</summary>
        public int Count { get; private set; }

        public GpuFlatTree()
        {
            NodeA = NewBuffer();
            NodeB = NewBuffer();
            NodeC = NewBuffer();
        }

        /// <summary>Converts <paramref name="flat"/> to the GPU layout in CPU memory (no GL calls).</summary>
        public void Pack(FlatTree flat)
        {
            int nodes = flat.Count;
            Count = nodes;
            if (_a.Length < nodes * 4)
            {
                int cap = nodes * 4 * 5 / 4 + 64;
                _a = new float[cap]; _b = new float[cap]; _c = new float[cap];
            }
            if (nodes >= ParallelPackThreshold)
            {
                int chunks = Environment.ProcessorCount * 2;
                int size = (nodes + chunks - 1) / chunks;
                Parallel.For(0, chunks, c => PackRange(flat, c * size, Math.Min(nodes, (c + 1) * size)));
            }
            else PackRange(flat, 0, nodes);
        }

        private void PackRange(FlatTree flat, int start, int end)
        {
            float[] na = _a, nb = _b, nc = _c;
            for (int i = start; i < end; i++)
            {
                int k = i * 4;
                Vector4 a = flat.ComMass[i], b = flat.CentreHalf[i];
                na[k] = a.X; na[k + 1] = a.Y; na[k + 2] = a.Z; na[k + 3] = a.W;
                nb[k] = b.X; nb[k + 1] = b.Y; nb[k + 2] = b.Z; nb[k + 3] = b.W;
                nc[k] = BitConverter.Int32BitsToSingle(flat.Skip[i]);
                nc[k + 1] = BitConverter.Int32BitsToSingle(flat.Body[i]);
                nc[k + 2] = flat.Soft2[i];
                nc[k + 3] = 0f;
            }
        }

        /// <summary>Uploads the last <see cref="Pack"/>, growing the buffers when needed.</summary>
        public void Upload()
        {
            int nodes = Count;
            if (nodes > _capacity)
            {
                _capacity = nodes * 5 / 4 + 64;
                foreach (int buf in new[] { NodeA, NodeB, NodeC })
                {
                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buf);
                    GL.BufferData(BufferTarget.ShaderStorageBuffer, _capacity * 16, IntPtr.Zero, BufferUsageHint.StreamDraw);
                }
            }
            if (nodes <= 0) return;
            Write(NodeA, _a, nodes * 4);
            Write(NodeB, _b, nodes * 4);
            Write(NodeC, _c, nodes * 4);
        }

        private static void Write(int buffer, float[] data, int floats)
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, floats * 4, data);
        }

        private static int NewBuffer()
        {
            int b = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, b);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, 16, IntPtr.Zero, BufferUsageHint.StreamDraw);
            return b;
        }

        public void Dispose()
        {
            foreach (int b in new[] { NodeA, NodeB, NodeC }) if (b != 0) GL.DeleteBuffer(b);
            NodeA = NodeB = NodeC = 0;
        }
    }
}
