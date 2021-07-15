// Copyright (c) Stride contributors (https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Animations;
using Stride.Core.Collections;
using Stride.Core.Mathematics;
using Stride.Rendering;
using static Stride.Importer.Gltf.GltfUtils;

namespace Stride.Importer.Gltf
{
    public static class GltfAnimationParser
    {
        /// <summary>
        /// Converts GLTF Skeleton into a Stride Skeleton
        /// </summary>
        /// <param name="root"></param>
        /// <returns>skeleton</returns>
        public static Skeleton ConvertSkeleton(SharpGLTF.Schema2.ModelRoot root)
        {
            Skeleton result = new Skeleton();
            var skin = root.LogicalNodes.First(x => x.Mesh == root.LogicalMeshes.First()).Skin;
            // If there is no corresponding skins return a skeleton with 2 bones (an empty skeleton would make the editor crash)
            if (skin == null)
            {
                result.Nodes = new List<ModelNodeDefinition>() {
                    new ModelNodeDefinition
                    {
                        Name = "root",
                        Flags = ModelNodeFlags.EnableRender,
                        ParentIndex = -1,
                        Transform = new TransformTRS
                        {
                            Position = Vector3.Zero,
                            Rotation = Quaternion.Identity,
                            Scale = Vector3.Zero
                        }
                    },
                    new ModelNodeDefinition
                    {
                        Name = "Mesh",
                        Flags = ModelNodeFlags.EnableRender,
                        ParentIndex = -1,
                        Transform = new TransformTRS
                        {
                            Position = Vector3.Zero,
                            Rotation = Quaternion.Identity,
                            Scale = Vector3.Zero
                        }
                    },
                }.ToArray();
                return result;
            }
            // for each joints we create a ModelNodeDefinition
            var jointList = Enumerable.Range(0, skin.JointsCount).Select(x => skin.GetJoint(x).Joint).ToList();
            var mnd =
                jointList
                .Select(
                    x =>
                    new ModelNodeDefinition
                    {
                        Name = x.Name ?? "Joint_" + x.LogicalIndex,
                        Flags = ModelNodeFlags.Default,
                        ParentIndex = jointList.IndexOf(x.VisualParent) + 1,
                        Transform = new TransformTRS
                        {
                            Position = ConvertNumerics(x.LocalTransform.Translation),
                            Rotation = ConvertNumerics(x.LocalTransform.Rotation),
                            Scale = ConvertNumerics(x.LocalTransform.Scale)
                        }

                    }
                )
                .ToList();
            // And insert a parent one not caught by the above function (GLTF does not consider the parent bone as a bone)
            mnd.Insert(
                    0,
                    new ModelNodeDefinition
                    {
                        Name = "Armature",
                        Flags = ModelNodeFlags.EnableRender,
                        ParentIndex = -1,
                        Transform = new TransformTRS
                        {
                            Position = Vector3.Zero,
                            Rotation = Quaternion.Identity,
                            Scale = Vector3.Zero
                        }
                    });
            result.Nodes = mnd.ToArray();
            return result;
        }

        /// <summary>
        /// Helper function to create a keyframe from values
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="keyTime"></param>
        /// <param name="value"></param>
        /// <returns>keyframe</returns>
        public static KeyFrameData<T> CreateKeyFrame<T>(float keyTime, T value)
        {
            return new KeyFrameData<T>((CompressedTimeSpan)TimeSpan.FromSeconds(keyTime), value);
        }

        /// <summary>
        /// Convert a GLTF animation channel into a Stride AnimationCurve.
        /// If the model has no skin, root motion should be enabled
        /// </summary>
        /// <param name="channels"></param>
        /// <param name="root"></param>
        /// <returns>animationCurves</returns>
        public static Dictionary<string, AnimationCurve> ConvertCurves(IReadOnlyList<SharpGLTF.Schema2.AnimationChannel> channels, SharpGLTF.Schema2.ModelRoot root)
        {
            var result = new Dictionary<string, AnimationCurve>();
            if (root.LogicalAnimations.Count == 0) return result;
            var skin = root.LogicalNodes.First(x => x.Mesh == root.LogicalMeshes.First()).Skin;

            // In case there is no skin joints/bones, animate transform component
            if (skin == null)
            {
                string base2 = "[TransformComponent].type";
                foreach (var chan in channels)
                {
                    switch (chan.TargetNodePath)
                    {
                        case SharpGLTF.Schema2.PropertyPath.translation:
                            result.Add(base2.Replace("type", "Position"), ConvertCurve(chan.GetTranslationSampler()));
                            break;
                        case SharpGLTF.Schema2.PropertyPath.rotation:
                            result.Add(base2.Replace("type", "Rotation"), ConvertCurve(chan.GetRotationSampler()));
                            break;
                        case SharpGLTF.Schema2.PropertyPath.scale:
                            result.Add(base2.Replace("type", "Scale"), ConvertCurve(chan.GetScaleSampler()));
                            break;
                    };
                }
                return result;
            }

            // Else we animate the model.
            string baseString = "[ModelComponent.Key].Skeleton.NodeTransformations[index].Transform.type";



            var jointList = Enumerable.Range(0, skin.JointsCount).Select(x => skin.GetJoint(x).Joint).ToList();
            foreach (var chan in channels)
            {
                var index = jointList.IndexOf(chan.TargetNode) + 1;
                switch (chan.TargetNodePath)
                {
                    case SharpGLTF.Schema2.PropertyPath.translation:
                        result.Add(baseString.Replace("index", $"{index}").Replace("type", "Position"), ConvertCurve(chan.GetTranslationSampler()));
                        break;
                    case SharpGLTF.Schema2.PropertyPath.rotation:
                        result.Add(baseString.Replace("index", $"{index}").Replace("type", "Rotation"), ConvertCurve(chan.GetRotationSampler()));
                        break;
                    case SharpGLTF.Schema2.PropertyPath.scale:
                        result.Add(baseString.Replace("index", $"{index}").Replace("type", "Scale"), ConvertCurve(chan.GetScaleSampler()));
                        break;
                };

            }
            return result;

        }

        /// <summary>
        /// Converts a GLTF AnimationSampler into a Stride AnimationCurve
        /// </summary>
        /// <param name="sampler"></param>
        /// <returns></returns>
        public static AnimationCurve<Quaternion> ConvertCurve(SharpGLTF.Schema2.IAnimationSampler<System.Numerics.Quaternion> sampler)
        {
            var interpolationType =
                sampler.InterpolationMode switch
                {
                    SharpGLTF.Schema2.AnimationInterpolationMode.LINEAR => AnimationCurveInterpolationType.Linear,
                    SharpGLTF.Schema2.AnimationInterpolationMode.STEP => AnimationCurveInterpolationType.Constant,
                    SharpGLTF.Schema2.AnimationInterpolationMode.CUBICSPLINE => AnimationCurveInterpolationType.Cubic,
                    _ => throw new NotImplementedException(),
                };

            var keyframes =
                interpolationType switch
                {
                    AnimationCurveInterpolationType.Constant =>
                        sampler.GetLinearKeys().Select(x => CreateKeyFrame(x.Key, ConvertNumerics(x.Value))),
                    AnimationCurveInterpolationType.Linear =>
                        sampler.GetLinearKeys().Select(x => CreateKeyFrame(x.Key, ConvertNumerics(x.Value))),
                    // Cubic might be broken
                    AnimationCurveInterpolationType.Cubic =>
                        sampler.GetCubicKeys().Select(x => ConvertNumerics(x.Value).Select(y => CreateKeyFrame(x.Key, y))).SelectMany(x => x),
                    _ => throw new NotImplementedException()
                };

            return new AnimationCurve<Quaternion>
            {
                InterpolationType = interpolationType,
                KeyFrames = new FastList<KeyFrameData<Quaternion>>(keyframes)
            };
        }

        /// <summary>
        /// Converts a GLTF AnimationSampler into a Stride AnimationCurve
        /// </summary>
        /// <param name="sampler"></param>
        /// <returns></returns>
        public static AnimationCurve<Vector3> ConvertCurve(SharpGLTF.Schema2.IAnimationSampler<System.Numerics.Vector3> sampler)
        {
            var interpolationType =
                sampler.InterpolationMode switch
                {
                    SharpGLTF.Schema2.AnimationInterpolationMode.LINEAR => AnimationCurveInterpolationType.Linear,
                    SharpGLTF.Schema2.AnimationInterpolationMode.STEP => AnimationCurveInterpolationType.Constant,
                    SharpGLTF.Schema2.AnimationInterpolationMode.CUBICSPLINE => AnimationCurveInterpolationType.Cubic,
                    _ => throw new NotImplementedException(),
                };

            var keyframes =
                interpolationType switch
                {
                    AnimationCurveInterpolationType.Constant =>
                        sampler.GetLinearKeys().Select(x => CreateKeyFrame(x.Key, ConvertNumerics(x.Value))),
                    AnimationCurveInterpolationType.Linear =>
                        sampler.GetLinearKeys().Select(x => CreateKeyFrame(x.Key, ConvertNumerics(x.Value))),
                    // TODO : Cubic can be broken
                    AnimationCurveInterpolationType.Cubic =>
                        sampler.GetCubicKeys().Select(x => ConvertNumerics(x.Value).Select(y => CreateKeyFrame(x.Key, y))).SelectMany(x => x),
                    _ => throw new NotImplementedException()
                };

            return new AnimationCurve<Vector3>
            {
                InterpolationType = interpolationType,
                KeyFrames = new FastList<KeyFrameData<Vector3>>(keyframes)
            };
        }
        /// <summary>
        /// Converts a GLTF AnimationSampler into a Stride AnimationCurve
        /// </summary>
        /// <param name="sampler"></param>
        /// <returns></returns>
        public static AnimationCurve<Vector2> ConvertCurve(SharpGLTF.Schema2.IAnimationSampler<System.Numerics.Vector2> sampler)
        {
            var interpolationType =
                sampler.InterpolationMode switch
                {
                    SharpGLTF.Schema2.AnimationInterpolationMode.LINEAR => AnimationCurveInterpolationType.Linear,
                    SharpGLTF.Schema2.AnimationInterpolationMode.STEP => AnimationCurveInterpolationType.Constant,
                    SharpGLTF.Schema2.AnimationInterpolationMode.CUBICSPLINE => AnimationCurveInterpolationType.Cubic,
                    _ => throw new NotImplementedException(),
                };

            var keyframes =
                interpolationType switch
                {
                    AnimationCurveInterpolationType.Constant =>
                        sampler.GetLinearKeys().Select(x => CreateKeyFrame(x.Key, ConvertNumerics(x.Value))),
                    AnimationCurveInterpolationType.Linear =>
                        sampler.GetLinearKeys().Select(x => CreateKeyFrame(x.Key, ConvertNumerics(x.Value))),
                    // TODO : Cubic can be broken
                    AnimationCurveInterpolationType.Cubic =>
                        sampler.GetCubicKeys().Select(x => ConvertNumerics(x.Value).Select(y => CreateKeyFrame(x.Key, y))).SelectMany(x => x),
                    _ => throw new NotImplementedException()
                };

            return new AnimationCurve<Vector2>
            {
                InterpolationType = interpolationType,
                KeyFrames = new FastList<KeyFrameData<Vector2>>(keyframes)
            };
        }
        /// <summary>
        /// Converts a GLTF AnimationSampler into a Stride AnimationCurve
        /// </summary>
        /// <param name="sampler"></param>
        /// <returns></returns>
        public static AnimationCurve<Vector4> ConvertCurve(SharpGLTF.Schema2.IAnimationSampler<System.Numerics.Vector4> sampler)
        {
            var interpolationType =
                sampler.InterpolationMode switch
                {
                    SharpGLTF.Schema2.AnimationInterpolationMode.LINEAR => AnimationCurveInterpolationType.Linear,
                    SharpGLTF.Schema2.AnimationInterpolationMode.STEP => AnimationCurveInterpolationType.Constant,
                    SharpGLTF.Schema2.AnimationInterpolationMode.CUBICSPLINE => AnimationCurveInterpolationType.Cubic,
                    _ => throw new NotImplementedException(),
                };

            var keyframes =
                interpolationType switch
                {
                    AnimationCurveInterpolationType.Constant =>
                        sampler.GetLinearKeys().Select(x => CreateKeyFrame(x.Key, ConvertNumerics(x.Value))),
                    AnimationCurveInterpolationType.Linear =>
                        sampler.GetLinearKeys().Select(x => CreateKeyFrame(x.Key, ConvertNumerics(x.Value))),
                    // TODO : Cubic can be broken
                    AnimationCurveInterpolationType.Cubic =>
                        sampler.GetCubicKeys().Select(x => ConvertNumerics(x.Value).Select(y => CreateKeyFrame(x.Key, y))).SelectMany(x => x),
                    _ => throw new NotImplementedException()
                };

            return new AnimationCurve<Vector4>
            {
                InterpolationType = interpolationType,
                KeyFrames = new FastList<KeyFrameData<Vector4>>(keyframes)
            };
        }
        /// <summary>
        /// Converts a GLTF AnimationSampler into a Stride AnimationCurve
        /// </summary>
        /// <param name="sampler"></param>
        /// <returns></returns>
        public static AnimationCurve<float> ConvertCurve(SharpGLTF.Schema2.IAnimationSampler<float> sampler)
        {
            var interpolationType =
                sampler.InterpolationMode switch
                {
                    SharpGLTF.Schema2.AnimationInterpolationMode.LINEAR => AnimationCurveInterpolationType.Linear,
                    SharpGLTF.Schema2.AnimationInterpolationMode.STEP => AnimationCurveInterpolationType.Constant,
                    SharpGLTF.Schema2.AnimationInterpolationMode.CUBICSPLINE => AnimationCurveInterpolationType.Cubic,
                    _ => throw new NotImplementedException(),
                };

            var keyframes =
                interpolationType switch
                {
                    AnimationCurveInterpolationType.Constant =>
                        sampler.GetLinearKeys().Select(x => CreateKeyFrame(x.Key, x.Value)),
                    AnimationCurveInterpolationType.Linear =>
                        sampler.GetLinearKeys().Select(x => CreateKeyFrame(x.Key, x.Value)),
                    _ => throw new NotImplementedException()
                };

            return new AnimationCurve<float>
            {
                InterpolationType = interpolationType,
                KeyFrames = new FastList<KeyFrameData<float>>(keyframes)
            };
        }
    }
}
