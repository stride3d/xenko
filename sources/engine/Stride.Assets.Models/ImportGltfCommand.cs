// Copyright (c) Stride contributors (https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Stride.Core.BuildEngine;
using Stride.Core.Serialization.Contents;
using Stride.Animations;
using Stride.Rendering;
using Stride.Importer.Gltf;

namespace Stride.Assets.Models
{
    [Description("Import GLTF")]
    public class ImportGltfCommand : ImportModelCommand
    {
        private static string[] supportedExtensions = GltfAssetImporter.FileExtensions.Split(';');

        /// <inheritdoc/>
        public override string Title { get { string title = "Import GLTF "; try { title += Path.GetFileName(SourcePath) ?? "[File]"; } catch { title += "[INVALID PATH]"; } return title; } }

        public static bool IsSupportingExtensions(string ext)
        {
            if (string.IsNullOrEmpty(ext))
                return false;

            var extToLower = ext.ToLowerInvariant();

            return supportedExtensions.Any(supExt => supExt.Equals(extToLower));
        }

        protected override Model LoadModel(ICommandContext commandContext, ContentManager contentManager)
        {
            var model = GltfMeshParser.LoadGltf(SourcePath);
            var sceneData = GltfMeshParser.LoadFirstModel(model);
            return sceneData;
        }

        protected override Dictionary<string, AnimationClip> LoadAnimation(ICommandContext commandContext, ContentManager contentManager, out TimeSpan duration)
        {
            var file = SharpGLTF.Schema2.ModelRoot.Load(SourcePath);
            var sceneData = GltfMeshParser.ConvertAnimations(file);
            duration = GltfMeshParser.GetAnimationDuration(file);
            return sceneData;
        }

        protected override Skeleton LoadSkeleton(ICommandContext commandContext, ContentManager contentManager)
        {
            var file = SharpGLTF.Schema2.ModelRoot.Load(SourcePath);
            var sceneData = GltfAnimationParser.ConvertSkeleton(file);
            return sceneData;
        }



        public override bool ShouldSpawnNewProcess()
        {
            return true;
        }

        public override string ToString()
        {
            return "Import GLTF " + base.ToString();
        }
    }
}
