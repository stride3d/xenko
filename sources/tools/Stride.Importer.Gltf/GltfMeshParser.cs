using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stride.Animations;
using Stride.Assets.Materials;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Graphics.Data;
using Stride.Importer.Common;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using static Stride.Importer.Gltf.GltfAnimationParser;
using static Stride.Importer.Gltf.GltfUtils;

namespace Stride.Importer.Gltf
{
    public class GltfMeshParser
    {
        public static SharpGLTF.Schema2.ModelRoot LoadGltf(Stride.Core.IO.UFile sourcePath)
        {

            switch (sourcePath.GetFileExtension())
            {
                case ".gltf":
                    return SharpGLTF.Schema2.ModelRoot.Load(sourcePath);
                case null:
                    return SharpGLTF.Schema2.ModelRoot.Load(sourcePath);
                case ".glb":
                    var fs = new FileStream(sourcePath.FullPath, FileMode.Open);
                    return SharpGLTF.Schema2.ModelRoot.ReadGLB(fs);
                default:
                    return null;
            }

        }
        public static Model LoadFirstModel(SharpGLTF.Schema2.ModelRoot root)
        {
            var result = new Model()
            {
                Meshes = root.LogicalMeshes[0].Primitives.Select(x => LoadMesh(x)).ToList()
            };
            result.Skeleton = ConvertSkeleton(root);
            return result;
        }
        public static string FirstModelName(SharpGLTF.Schema2.ModelRoot root)
        {
            return root.LogicalMeshes.First()?.Name ?? "Mesh";
        }

        public static TimeSpan GetAnimationDuration(SharpGLTF.Schema2.ModelRoot root)
        {
            var time = root.LogicalAnimations.Select(x => x.Duration).Sum();
            return TimeSpan.FromSeconds(time);
        }

        public static EntityInfo ExtractEntityInfo(SharpGLTF.Schema2.ModelRoot modelRoot, UFile sourcePath)
        {
            SharpGLTF.Schema2.Skin skin = null;
            HashSet<String> boneNames = new HashSet<string>();
            List<NodeInfo> nodes = new List<NodeInfo>();

            var meshName = FirstModelName(modelRoot);
            var modelName = modelRoot.LogicalMeshes[0].Name ?? "Mesh";
            if (modelRoot.LogicalSkins.Where(x => x.VisualParents.First()?.Mesh == modelRoot.LogicalMeshes[0]).Count() > 0)
                skin =
                    modelRoot.LogicalSkins
                    .Where(x => x.VisualParents.First().Mesh == modelRoot.LogicalMeshes[0])
                    .First();

            if (skin != null)
            {
                boneNames =
                    Enumerable.Range(0, skin.JointsCount)
                    .Select(x => skin.GetJoint(x).Joint.Name ?? "Joint_" + skin.GetJoint(x).Joint.LogicalIndex)
                    .ToHashSet();
                nodes = Enumerable.Range(0, skin.JointsCount)
                    .Select(x => new NodeInfo() { Name = skin.GetJoint(x).Joint.Name ?? "Joint_" + skin.GetJoint(x).Joint.LogicalIndex, Depth = skin.GetJoint(x).Joint.LogicalIndex, Preserve = true })
                    .ToList();
            }
            
            var meshes =
                modelRoot
                .LogicalMeshes[0].Primitives
                .Select(
                    x => 
                    {
                        var materialName = x.Material != null ? FirstModelName(modelRoot) + "_" + (x.Material.Name ?? "Material" + x.Material.LogicalIndex) : "";
                        return (meshName + "_" + x.LogicalIndex, materialName);
                    }
                )
                .Select(
                    x =>
                    new MeshParameters()
                    {
                        MeshName = x.Item1,
                        BoneNodes = boneNames,
                        MaterialName = x.materialName,
                        NodeName = ""
                    }
                 )
                .ToList();

            List<String> animNodes =
                modelRoot.LogicalAnimations.Select(x => x.Name == null ? meshName + "_Animation_" + x.LogicalIndex : meshName + "_" + x.Name).ToList();

            var entityInfo = new EntityInfo
            {
                Models = meshes,
                AnimationNodes = animNodes,
                Materials = LoadMaterials(modelRoot, sourcePath),
                Nodes = nodes,
                TextureDependencies = GenerateTextureFullPaths(modelRoot, sourcePath)
            };
            return entityInfo;
        }




        public static MeshSkinningDefinition ConvertInverseBindMatrices(SharpGLTF.Schema2.ModelRoot root)
        {
            var skin = root.LogicalNodes.First(x => x.Mesh == root.LogicalMeshes[0]).Skin;
            if (skin == null) return null;
            var jointList = Enumerable.Range(0, skin.JointsCount).Select(skin.GetJoint);
            var mnt =
                new MeshSkinningDefinition
                {
                    Bones =
                        jointList
                            .Select((x, i) =>
                                new MeshBoneDefinition
                                {
                                    NodeIndex = i + 1,
                                    LinkToMeshMatrix = ConvertNumerics(x.InverseBindMatrix)
                                }
                            )
                            .ToArray()
                };
            return mnt;
        }

        public static Dictionary<string, AnimationClip> ConvertAnimations(SharpGLTF.Schema2.ModelRoot root)
        {
            var animations = root.LogicalAnimations;
            var meshName = FirstModelName(root);

            var clips =
                animations
                .Select(x =>
                   {
                       //Create animation clip with 
                       var clip = new AnimationClip { Duration = TimeSpan.FromSeconds(x.Duration) };
                       clip.RepeatMode = AnimationRepeatMode.LoopInfinite;
                       // Add Curve
                       ConvertCurves(x.Channels, root).ToList().ForEach(v => clip.AddCurve(v.Key, v.Value));
                       string name = x.Name == null ? meshName + "_Animation_" + x.LogicalIndex : meshName + "_" + x.Name;
                       clip.Optimize();
                       return (name, clip);
                   }
                )
                .ToList()
                .ToDictionary(x => x.name, x => x.clip);
            return clips;
        }

        public static Dictionary<string, MaterialAsset> LoadMaterials(SharpGLTF.Schema2.ModelRoot root, UFile sourcePath)
        {
            var result = new Dictionary<string, MaterialAsset>();
            foreach (var mat in root.LogicalMaterials)
            {
                var material = new MaterialAsset
                {
                    Attributes = new MaterialAttributes()
                };
                foreach (var chan in mat.Channels)
                {

                    if (chan.Texture != null && !chan.HasDefaultContent)
                    {

                        var gltfImg = chan.Texture.PrimaryImage;
                        string imgPath;
                        if (gltfImg.Content.SourcePath == null)
                        {
                            var textureName = gltfImg.Name ?? FirstModelName(root) + "_" + (mat.Name ?? mat.LogicalIndex.ToString()) + "_" + chan.Key;
                            imgPath = Path.Join(sourcePath.GetFullDirectory(), textureName + "." + gltfImg.Content.FileExtension);
                            if(!File.Exists(imgPath))
                                gltfImg.Content.SaveToFile(imgPath);
                        }
                        else
                        {
                            imgPath = gltfImg.Content.SourcePath;
                        }

                        switch (chan.Key)
                        {
                            case "BaseColor":
                                material.Attributes.Diffuse = new MaterialDiffuseMapFeature(GenerateTextureColor(imgPath, (TextureCoordinate)chan.TextureCoordinate, Vector2.One));
                                material.Attributes.DiffuseModel = new MaterialDiffuseLambertModelFeature();
                                break;
                            case "MetallicRoughness":
                                material.Attributes.MicroSurface = new MaterialGlossinessMapFeature(GenerateTextureScalar(imgPath, (TextureCoordinate)chan.TextureCoordinate, Vector2.One));
                                break;
                            case "Normal":
                                material.Attributes.Surface = new MaterialNormalMapFeature(GenerateTextureColor(imgPath, (TextureCoordinate)chan.TextureCoordinate, Vector2.One));
                                break;
                            case "Occlusion":
                                material.Attributes.Occlusion = new MaterialOcclusionMapFeature();
                                break;
                            case "Emissive":
                                material.Attributes.Emissive = new MaterialEmissiveMapFeature(GenerateTextureColor(imgPath, (TextureCoordinate)chan.TextureCoordinate, Vector2.One));
                                break;
                        }
                    }
                    else if (chan.Texture == null && !chan.HasDefaultContent)
                    {
                        var vt = new ComputeColor(new Color(ConvertNumerics(chan.Parameter)));
                        var x = new ComputeFloat(chan.Parameter.X);
                        switch (chan.Key)
                        {
                            case "BaseColor":
                                material.Attributes.Diffuse = new MaterialDiffuseMapFeature(vt);
                                material.Attributes.DiffuseModel = new MaterialDiffuseLambertModelFeature();
                                break;
                            case "MetallicRoughness":
                                material.Attributes.MicroSurface = new MaterialGlossinessMapFeature(x);
                                break;
                            case "Normal":
                                material.Attributes.Surface = new MaterialNormalMapFeature(vt);
                                break;
                            case "Occlusion":
                                material.Attributes.Occlusion = new MaterialOcclusionMapFeature();
                                break;
                            case "Emissive":
                                material.Attributes.Emissive = new MaterialEmissiveMapFeature(vt);
                                break;
                        }
                    }

                }
                material.Attributes.CullMode = CullMode.Back;
                var materialName = FirstModelName(root) + "_" + (mat.Name ?? "Material" + mat.LogicalIndex);

                result.Add(materialName, material);
            }
            return result;
        }

        public static Mesh LoadMesh(SharpGLTF.Schema2.MeshPrimitive mesh)
        {

            var draw = new MeshDraw
            {
                PrimitiveType = ConvertPrimitiveType(mesh.DrawPrimitiveType),
                IndexBuffer = ConvertSerializedIndexBufferBinding(mesh),
                VertexBuffers = ConvertSerializedVertexBufferBinding(mesh),
                DrawCount = GetDrawCount(mesh)
            };



            var result = new Mesh(draw, new ParameterCollection())
            {
                Skinning = ConvertInverseBindMatrices(mesh.LogicalParent.LogicalParent),
                Name = mesh.LogicalParent.Name,
                MaterialIndex = mesh.LogicalParent.LogicalParent.LogicalMaterials.ToList().IndexOf(mesh.Material)
            };


            //TODO : Add parameter collection only after checking if it has
            result.Parameters.Set(MaterialKeys.HasSkinningPosition, true);
            result.Parameters.Set(MaterialKeys.HasSkinningNormal, true);
            return result;
        }

        private static int GetDrawCount(SharpGLTF.Schema2.MeshPrimitive mesh)
        {
            var tmp = mesh.GetTriangleIndices().Select(x => new int[] { x.A, x.C, x.B }).SelectMany(x => x).Select(x => (uint)x).ToArray();
            return mesh.GetTriangleIndices().Select(x => new int[] { x.A, x.C, x.B }).SelectMany(x => x).Select(x => (uint)x).ToArray().Length;
        }

        public static IndexBufferBinding ConvertSerializedIndexBufferBinding(SharpGLTF.Schema2.MeshPrimitive mesh)
        {
            var indices =
                mesh.GetTriangleIndices()
                .Select(x => new int[] { x.A, x.C, x.B })
                .SelectMany(x => x).Select(x => (uint)x)
                .Select(BitConverter.GetBytes)
                .SelectMany(x => x).ToArray();
            var buf = GraphicsSerializerExtensions.ToSerializableVersion(new BufferData(BufferFlags.IndexBuffer, indices));
            return new IndexBufferBinding(buf, true, indices.Length);
        }

        public static VertexBufferBinding[] ConvertSerializedVertexBufferBinding(SharpGLTF.Schema2.MeshPrimitive mesh)
        {
            var offset = 0;
            var vertElem =
                mesh.VertexAccessors
                .Select(
                    x =>
                    {
                        var y = ConvertVertexElement(x, offset);
                        offset += y.Item2;
                        return y.Item1;
                    })
                .ToList();

            var declaration =
                new VertexDeclaration(
                    vertElem.ToArray()
            );

            var size = mesh.VertexAccessors.First().Value.Count;
            var byteBuffer = Enumerable.Range(0, size)
                .Select(
                    x =>
                        declaration.EnumerateWithOffsets()
                            .Select(y => y.VertexElement.SemanticName
                                .Replace("ORD", "ORD_" + y.VertexElement.SemanticIndex)
                                .Replace("BLENDINDICES", "JOINTS_0")
                                .Replace("BLENDWEIGHT", "WEIGHTS_0")
                            )
                            .Select(y => mesh.GetVertexAccessor(y)?.TryGetVertexBytes(x).ToArray())
                            .Where(x => x != null)
                )
                .SelectMany(x => x)
                .SelectMany(x => x)
                .ToArray();

            var buffer =
                GraphicsSerializerExtensions.ToSerializableVersion(
                    new BufferData(BufferFlags.VertexBuffer, byteBuffer)
                );
            var binding = new VertexBufferBinding(buffer, declaration, size);

            return new List<VertexBufferBinding>() { binding }.ToArray();
        }


    }
}
