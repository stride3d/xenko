// Copyright (c) Stride contributors (https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

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
    public static class GltfMeshParser
    {
        /// <summary>
        /// Loads the gltf file depending its extension.
        /// </summary>
        /// <param name="sourcePath"> path of the gltf file</param>
        /// <returns>ModelRoot</returns>
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

        /// <summary>
        /// Converts the first mesh in the GLTF file into a stride Model 
        /// </summary>
        /// <param name="root"></param>
        /// <returns></returns>
        public static Model LoadFirstModel(SharpGLTF.Schema2.ModelRoot root)
        {
            // We load every primitives of the first mesh
            var result = new Model()
            {
                Meshes = root.LogicalMeshes[0].Primitives.Select(x => LoadMesh(x)).ToList()
            };
            result.Skeleton = ConvertSkeleton(root);
            return result;
        }

        /// <summary>
        /// Gets the first model name
        /// </summary>
        /// <param name="root"></param>
        /// <returns></returns>
        public static string FirstModelName(SharpGLTF.Schema2.ModelRoot root)
        {
            // TODO : Get the file name instead of `Mesh`
            return root.LogicalMeshes.First()?.Name ?? "Mesh";
        }

        /// <summary>
        /// Gets the sum of all animation duration
        /// </summary>
        /// <param name="root"></param>
        /// <returns></returns>
        public static TimeSpan GetAnimationDuration(SharpGLTF.Schema2.ModelRoot root)
        {
            var time = root.LogicalAnimations.Select(x => x.Duration).Sum();
            return TimeSpan.FromSeconds(time);
        }

        /// <summary>
        /// Extract the entity info. This function tells the editor informations about any assets.
        /// If any info is missing/wrong the assests won't be correctly imported.
        /// </summary>
        /// <param name="modelRoot"></param>
        /// <param name="sourcePath"></param>
        /// <returns></returns>
        public static EntityInfo ExtractEntityInfo(SharpGLTF.Schema2.ModelRoot modelRoot, UFile sourcePath)
        {
            SharpGLTF.Schema2.Skin skin = null;
            HashSet<string> boneNames = new HashSet<string>();
            List<NodeInfo> nodes = new List<NodeInfo>();

            var meshName = FirstModelName(modelRoot);


            if (modelRoot.LogicalSkins.Where(x => x.VisualParents.First()?.Mesh == modelRoot.LogicalMeshes[0]).Count() > 0)
                skin =
                    modelRoot.LogicalSkins
                    .Where(x => x.VisualParents.First().Mesh == modelRoot.LogicalMeshes[0])
                    .First();

            // If there is a skin, we can load the bone names and instantiate the node informations.
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

            // Loading Mesh parameters, this will link the materials with the meshes
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

            // Loading the animation names (should be the same as the keys used in animations
            List<string> animNodes =
                ConvertAnimations(modelRoot).Keys.ToList();

            return new EntityInfo
            {
                Models = meshes,
                AnimationNodes = animNodes,
                Materials = LoadMaterials(modelRoot, sourcePath),
                Nodes = nodes,
                TextureDependencies = GenerateTextureFullPaths(modelRoot, sourcePath)
            };
        }



        /// <summary>
        /// Converts GLTF joints into MeshSkinningDefinition, defining the Mesh to World matrix useful for skinning and animations.
        /// </summary>
        /// <param name="root"></param>
        /// <returns></returns>
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

        /// <summary>
        /// Converts GLTF animations into Stride AnimationClips.
        /// </summary>
        /// <param name="root"></param>
        /// <returns></returns>
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

        /// <summary>
        /// Convert GLTF materials to Stride materials
        /// </summary>
        /// <param name="root"></param>
        /// <param name="sourcePath"></param>
        /// <returns></returns>
        public static Dictionary<string, MaterialAsset> LoadMaterials(SharpGLTF.Schema2.ModelRoot root, UFile sourcePath)
        {
            // TODO : Handle official ClearCoat extension
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
                        var textureName = gltfImg.Name ?? FirstModelName(root) + "_" + (mat.Name ?? mat.LogicalIndex.ToString()) + "_" + chan.Key;

                        string imgPath;
                        if (gltfImg.Content.SourcePath == null)
                        {
                            imgPath = Path.Join(sourcePath.GetFullDirectory(), textureName + "." + gltfImg.Content.FileExtension);
                            if (!File.Exists(imgPath))
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
                var materialName = FirstModelName(root) + "_" + (mat.Name ?? "Material") + "_" + mat.LogicalIndex;

                result.TryAdd(materialName, material);
            }
            return result;
        }

        /// <summary>
        /// Convert a GLTF Primitive into a Stride Mesh
        /// </summary>
        /// <param name="mesh"></param>
        /// <returns></returns>
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


            // TODO : Add parameter collection only after checking if it has
            result.Parameters.Set(MaterialKeys.HasSkinningPosition, true);
            result.Parameters.Set(MaterialKeys.HasSkinningNormal, true);
            return result;
        }

        /// <summary>
        /// Gets the number of triangle indices.
        /// </summary>
        /// <param name="mesh"></param>
        /// <returns></returns>
        private static int GetDrawCount(SharpGLTF.Schema2.MeshPrimitive mesh)
        {
            // TODO : Check if every meshes has triangle indices
            return mesh.GetTriangleIndices().Select(x => new int[] { x.A, x.B, x.C }).SelectMany(x => x).Select(x => (uint)x).ToArray().Length;
        }

        /// <summary>
        /// Converts an index buffer into a serialized index buffer binding for asset creation.
        /// </summary>
        /// <param name="mesh"></param>
        /// <returns></returns>
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

        /// <summary>
        /// Converts a vertex buffer into a serialized vertex buffer for asset creation.
        /// </summary>
        /// <param name="mesh"></param>
        /// <returns></returns>
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

            List<byte> bytelst = new();

            for (int i = 0; i < size; i++)
            {
                foreach (var e in mesh.VertexAccessors.Keys)
                {
                    var bytes = mesh.GetVertexAccessor(e).TryGetVertexBytes(i).ToArray();
                    bytelst.AddRange(bytes);
                }
            }
            var byteBuffer = bytelst.ToArray();

            var buffer =
                GraphicsSerializerExtensions.ToSerializableVersion(
                    new BufferData(BufferFlags.VertexBuffer, byteBuffer)
                );
            var binding = new VertexBufferBinding(buffer, declaration, size);

            return new List<VertexBufferBinding>() { binding }.ToArray();
        }


    }
}
