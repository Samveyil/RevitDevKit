using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitDevKit.Core
{
    // Draws ShownGeometry over the model through DirectContext3D, Revit's way of drawing graphics that are not
    // elements. Segments go in the opaque pass, faces see-through in the transparent one. Buffers are rebuilt only
    // when what is shown changes; an index buffer holds 16-bit indices, so large shapes go in pieces.
    public sealed class ShownGeometryServer : IDirectContext3DServer
    {
        private static readonly Guid ServerId = new Guid("b3c55f2e-7a4d-4b91-9e0f-2d6f7e1c8a42");

        // Of 65 536 a buffer may index; lines and triangles both end on a whole primitive within it.
        private const int MaxVertices = 65000;

        // 0 opaque, 255 invisible: faces let the model show through.
        private const uint FaceTransparency = 150;

        private static ShownGeometryServer _instance;

        private readonly object _sync = new object();
        private int _builtVersion = -1;
        private List<Batch> _lines = new List<Batch>();
        private List<Batch> _faces = new List<Batch>();
        private Outline _bounds;

        public static void Register()
        {
            if (_instance != null) return;
            try
            {
                var service = (MultiServerService)ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService);
                _instance = new ShownGeometryServer();
                service.AddServer(_instance);

                var active = service.GetActiveServerIds();
                active.Add(ServerId);
                service.SetActiveServers(active);
            }
            catch (Exception ex)
            {
                _instance = null;
                ConsoleOutput.Debug("Show(...) не будет рисовать: сервер DirectContext3D не зарегистрирован: " + ex.Message);
            }
        }

        public static void Unregister()
        {
            if (_instance == null) return;
            try
            {
                var service = (MultiServerService)ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService);
                service.RemoveServer(ServerId);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Сервер DirectContext3D не снят: " + ex.Message);
            }

            _instance.Release();
            _instance = null;
        }

        public Guid GetServerId() => ServerId;
        public ExternalServiceId GetServiceId() => ExternalServices.BuiltInExternalServices.DirectContext3DService;
        public string GetName() => "DevKit Show";
        public string GetVendorId() => "RevitDevKit";
        public string GetDescription() => "Геометрия, показанная скриптами DevKit через Show(...)";
        public string GetApplicationId() => string.Empty;
        public string GetSourceId() => string.Empty;
        public bool UsesHandles() => false;

        public bool CanExecute(View view)
        {
            var document = ShownGeometry.Document;
            if (document == null || ShownGeometry.Snapshot().Count == 0) return false;
            try { return view.Document.Equals(document); }
            catch { return false; }
        }

        public Outline GetBoundingBox(View view)
        {
            Build();
            return _bounds;
        }

        public bool UseInTransparentPass(View view)
        {
            Build();
            return _faces.Count > 0;
        }

        public void RenderScene(View view, DisplayStyle displayStyle)
        {
            try
            {
                Build();
                var transparent = DrawContext.IsTransparentPass();
                lock (_sync)
                {
                    foreach (var batch in transparent ? _faces : _lines)
                    {
                        if (!batch.Vertices.IsValid() || !batch.Indices.IsValid()) continue;
                        DrawContext.FlushBuffer(batch.Vertices, batch.VertexCount, batch.Indices, batch.IndexCount,
                            batch.Format, batch.Effect, batch.Primitive, 0, batch.PrimitiveCount);
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleOutput.Debug("Show(...) не нарисовал: " + ex.Message);
            }
        }

        private void Build()
        {
            if (_builtVersion == ShownGeometry.Version) return;

            lock (_sync)
            {
                var version = ShownGeometry.Version;
                var shapes = ShownGeometry.Snapshot();
                Release();

                foreach (var shape in shapes)
                {
                    _lines.AddRange(LineBatches(shape));
                    _faces.AddRange(FaceBatches(shape));
                }

                _bounds = Bounds(shapes);
                _builtVersion = version;
            }
        }

        private void Release()
        {
            foreach (var batch in _lines.Concat(_faces)) batch.Dispose();
            _lines = new List<Batch>();
            _faces = new List<Batch>();
            _bounds = null;
        }

        private static IEnumerable<Batch> LineBatches(ShownShape shape)
        {
            var color = new ColorWithTransparency(shape.Color.Red, shape.Color.Green, shape.Color.Blue, 0);
            var step = MaxVertices - MaxVertices % 2;
            for (var start = 0; start < shape.Lines.Count; start += step)
            {
                var count = Math.Min(step, shape.Lines.Count - start);
                var vertices = new VertexBuffer(count * VertexPositionColored.GetSizeInFloats());
                vertices.Map(count * VertexPositionColored.GetSizeInFloats());
                var stream = vertices.GetVertexStreamPositionColored();
                for (var i = 0; i < count; i++)
                    stream.AddVertex(new VertexPositionColored(shape.Lines[start + i], color));
                vertices.Unmap();

                var lines = count / 2;
                var indices = new IndexBuffer(lines * IndexLine.GetSizeInShortInts());
                indices.Map(lines * IndexLine.GetSizeInShortInts());
                var indexStream = indices.GetIndexStreamLine();
                for (var i = 0; i < lines; i++)
                    indexStream.AddLine(new IndexLine(2 * i, 2 * i + 1));
                indices.Unmap();

                yield return new Batch(vertices, count, indices, lines * IndexLine.GetSizeInShortInts(), VertexFormatBits.PositionColored,
                    PrimitiveType.LineList, lines, null);
            }
        }

        private static IEnumerable<Batch> FaceBatches(ShownShape shape)
        {
            var color = new ColorWithTransparency(shape.Color.Red, shape.Color.Green, shape.Color.Blue, FaceTransparency);
            var step = MaxVertices - MaxVertices % 3;
            for (var start = 0; start < shape.Triangles.Count; start += step)
            {
                var count = Math.Min(step, shape.Triangles.Count - start);
                count -= count % 3;
                if (count == 0) yield break;

                var vertices = new VertexBuffer(count * VertexPositionNormalColored.GetSizeInFloats());
                vertices.Map(count * VertexPositionNormalColored.GetSizeInFloats());
                var stream = vertices.GetVertexStreamPositionNormalColored();
                for (var i = 0; i < count; i += 3)
                {
                    var a = shape.Triangles[start + i];
                    var b = shape.Triangles[start + i + 1];
                    var c = shape.Triangles[start + i + 2];
                    var normal = (b - a).CrossProduct(c - a);
                    normal = normal.GetLength() > 1e-12 ? normal.Normalize() : XYZ.BasisZ;
                    stream.AddVertex(new VertexPositionNormalColored(a, normal, color));
                    stream.AddVertex(new VertexPositionNormalColored(b, normal, color));
                    stream.AddVertex(new VertexPositionNormalColored(c, normal, color));
                }
                vertices.Unmap();

                var triangles = count / 3;
                var indices = new IndexBuffer(triangles * IndexTriangle.GetSizeInShortInts());
                indices.Map(triangles * IndexTriangle.GetSizeInShortInts());
                var indexStream = indices.GetIndexStreamTriangle();
                for (var i = 0; i < triangles; i++)
                    indexStream.AddTriangle(new IndexTriangle(3 * i, 3 * i + 1, 3 * i + 2));
                indices.Unmap();

                yield return new Batch(vertices, count, indices, triangles * IndexTriangle.GetSizeInShortInts(), VertexFormatBits.PositionNormalColored,
                    PrimitiveType.TriangleList, triangles, FaceTransparency / 255.0);
            }
        }

        private static Outline Bounds(IEnumerable<ShownShape> shapes)
        {
            Outline bounds = null;
            foreach (var point in shapes.SelectMany(s => s.Lines.Concat(s.Triangles)))
            {
                if (bounds == null) bounds = new Outline(point, point);
                else bounds.AddPoint(point);
            }

            return bounds;
        }

        private sealed class Batch : IDisposable
        {
            public Batch(VertexBuffer vertices, int vertexCount, IndexBuffer indices, int indexCount, VertexFormatBits format,
                PrimitiveType primitive, int primitiveCount, double? transparency)
            {
                Vertices = vertices;
                VertexCount = vertexCount;
                Indices = indices;
                IndexCount = indexCount;
                Format = new VertexFormat(format);
                Effect = new EffectInstance(format);
                if (transparency.HasValue) Effect.SetTransparency(transparency.Value);
                Primitive = primitive;
                PrimitiveCount = primitiveCount;
            }

            public VertexBuffer Vertices { get; }
            public int VertexCount { get; }
            public IndexBuffer Indices { get; }
            public int IndexCount { get; }
            public VertexFormat Format { get; }
            public EffectInstance Effect { get; }
            public PrimitiveType Primitive { get; }
            public int PrimitiveCount { get; }

            public void Dispose()
            {
                Vertices.Dispose();
                Indices.Dispose();
                Format.Dispose();
                Effect.Dispose();
            }
        }
    }
}
