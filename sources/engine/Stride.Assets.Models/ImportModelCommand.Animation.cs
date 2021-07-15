// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core.BuildEngine;
using Stride.Core.Collections;
using Stride.Core.Extensions;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Updater;
using Stride.Animations;
using Stride.Rendering;

namespace Stride.Assets.Models
{
    public partial class ImportModelCommand
    {
        public AnimationRepeatMode AnimationRepeatMode { get; set; }
        public bool AnimationRootMotion { get; set; }
        public TimeSpan StartFrame { get; set; } = TimeSpan.Zero;
        public TimeSpan EndFrame { get; set; } = AnimationAsset.LongestTimeSpan;

        public bool ImportCustomAttributes { get; set; }

        private unsafe object ExportAnimation(ICommandContext commandContext, ContentManager contentManager, bool failOnEmptyAnimation)
        {
            // Read from model file
            var modelSkeleton = LoadSkeleton(commandContext, contentManager); // we get model skeleton to compare it to real skeleton we need to map to
            AdjustSkeleton(modelSkeleton);

            TimeSpan duration;
            var animationClips = LoadAnimation(commandContext, contentManager, out duration);

            // Fix the animation frames
            double startFrameSeconds = StartFrame.TotalSeconds;
            double endFrameSeconds = EndFrame.TotalSeconds;
            var startTime = CompressedTimeSpan.FromSeconds(-startFrameSeconds);

            foreach (var clip in animationClips)
            {
                foreach (var animationCurve in clip.Value.Curves)
                {
                    animationCurve.ShiftKeys(startTime);
                }
            }

            var durationTimeSpan = TimeSpan.FromSeconds((endFrameSeconds - startFrameSeconds));
            if (duration > durationTimeSpan)
                duration = durationTimeSpan;

            var animationClip = new AnimationClip { Duration = duration };

            if (animationClips.Count > 0)
            {
                AnimationClip rootMotionAnimationClip = null;

                // If root motion is explicitely enabled, or if there is no skeleton, try to find root node and apply animation directly on TransformComponent
                if ((AnimationRootMotion || SkeletonUrl == null) && modelSkeleton.Nodes.Length >= 1)
                {
                    // No skeleton, map root node only
                    // TODO: For now, it seems to be located on node 1 in FBX files. Need to check if always the case, and what happens with Assimp
                    var rootNode0 = modelSkeleton.Nodes.Length >= 1 ? modelSkeleton.Nodes[0].Name : null;
                    var rootNode1 = modelSkeleton.Nodes.Length >= 2 ? modelSkeleton.Nodes[1].Name : null;
                    if ((rootNode0 != null && animationClips.TryGetValue(rootNode0, out rootMotionAnimationClip))
                        || (rootNode1 != null && animationClips.TryGetValue(rootNode1, out rootMotionAnimationClip)))
                    {
                        foreach (var channel in rootMotionAnimationClip.Channels)
                        {
                            var curve = rootMotionAnimationClip.Curves[channel.Value.CurveIndex];

                            // Root motion
                            var channelName = channel.Key;
                            if (channelName.StartsWith("Transform."))
                            {
                                animationClip.AddCurve($"[TransformComponent.Key]." + channelName.Replace("Transform.", string.Empty), curve);
                            }

                            // Also apply Camera curves
                            // TODO: Add some other curves?
                            if (channelName.StartsWith("Camera."))
                            {
                                animationClip.AddCurve($"[CameraComponent.Key]." + channelName.Replace("Camera.", string.Empty), curve);
                            }
                        }
                    }
                }

                // Load asset reference skeleton
                if (SkeletonUrl != null)
                {
                    var skeleton = contentManager.Load<Skeleton>(SkeletonUrl);
                    var skeletonMapping = new SkeletonMapping(skeleton, modelSkeleton);

                    // Process missing nodes
                    foreach (var nodeAnimationClipEntry in animationClips)
                    {
                        var nodeName = nodeAnimationClipEntry.Key;
                        var nodeAnimationClip = nodeAnimationClipEntry.Value;
                        var nodeIndex = modelSkeleton.Nodes.IndexOf(x => x.Name == nodeName);

                        // Node doesn't exist in skeleton? skip it
                        if (nodeIndex == -1 || skeletonMapping.SourceToSource[nodeIndex] != nodeIndex)
                            continue;

                        // Skip root motion node (if any)
                        if (nodeAnimationClip == rootMotionAnimationClip)
                            continue;

                        // Find parent node
                        var parentNodeIndex = modelSkeleton.Nodes[nodeIndex].ParentIndex;

                        if (parentNodeIndex != -1 && skeletonMapping.SourceToSource[parentNodeIndex] != parentNodeIndex)
                        {
                            // Some nodes were removed, we need to concat the anim curves
                            var currentNodeIndex = nodeIndex;
                            var nodesToMerge = new List<Tuple<ModelNodeDefinition, AnimationBlender, AnimationClipEvaluator>>();
                            while (currentNodeIndex != -1 && currentNodeIndex != skeletonMapping.SourceToSource[parentNodeIndex])
                            {
                                AnimationClip animationClipToMerge;
                                AnimationClipEvaluator animationClipEvaluator = null;
                                AnimationBlender animationBlender = null;
                                if (animationClips.TryGetValue(modelSkeleton.Nodes[currentNodeIndex].Name, out animationClipToMerge))
                                {
                                    animationBlender = new AnimationBlender();
                                    animationClipEvaluator = animationBlender.CreateEvaluator(animationClipToMerge);
                                }
                                nodesToMerge.Add(Tuple.Create(modelSkeleton.Nodes[currentNodeIndex], animationBlender, animationClipEvaluator));
                                currentNodeIndex = modelSkeleton.Nodes[currentNodeIndex].ParentIndex;
                            }

                            // Put them in proper parent to children order
                            nodesToMerge.Reverse();

                            // Find all key times
                            // TODO: We should detect discontinuities and keep them
                            var animationKeysSet = new HashSet<CompressedTimeSpan>();

                            foreach (var node in nodesToMerge)
                            {
                                if (node.Item3 != null)
                                    foreach (var curve in node.Item3.Clip.Curves)
                                    {
                                        foreach (CompressedTimeSpan time in curve.Keys)
                                        {
                                            animationKeysSet.Add(time);
                                        }
                                    }
                            }

                            // Sort key times
                            var animationKeys = animationKeysSet.ToList();
                            animationKeys.Sort();

                            var animationOperations = new FastList<AnimationOperation>();

                            var combinedAnimationClip = new AnimationClip();

                            var translationCurve = new AnimationCurve<Vector3>();
                            var rotationCurve = new AnimationCurve<Quaternion>();
                            var scaleCurve = new AnimationCurve<Vector3>();

                            // Evaluate at every key frame
                            foreach (var animationKey in animationKeys)
                            {
                                var matrix = Matrix.Identity;

                                // Evaluate node
                                foreach (var node in nodesToMerge)
                                {
                                    // Needs to be an array in order for it to be modified by the UpdateEngine, otherwise it would get passed by value
                                    var modelNodeDefinitions = new ModelNodeDefinition[1] { node.Item1 };

                                    if (node.Item2 != null && node.Item3 != null)
                                    {
                                        // Compute
                                        AnimationClipResult animationClipResult = null;
                                        animationOperations.Clear();
                                        animationOperations.Add(AnimationOperation.NewPush(node.Item3, animationKey));
                                        node.Item2.Compute(animationOperations, ref animationClipResult);

                                        var updateMemberInfos = new List<UpdateMemberInfo>();
                                        foreach (var channel in animationClipResult.Channels)
                                        {
                                            if (channel.IsUserCustomProperty)
                                                continue;

                                            updateMemberInfos.Add(new UpdateMemberInfo { Name = "[0]." + channel.PropertyName, DataOffset = channel.Offset });
                                        }

                                        // TODO: Cache this
                                        var compiledUpdate = UpdateEngine.Compile(typeof(ModelNodeDefinition[]), updateMemberInfos);

                                        fixed (byte* data = animationClipResult.Data)
                                        {
                                            UpdateEngine.Run(modelNodeDefinitions, compiledUpdate, (IntPtr)data, null);
                                        }
                                    }

                                    Matrix localMatrix;
                                    var transformTRS = modelNodeDefinitions[0].Transform;
                                    Matrix.Transformation(ref transformTRS.Scale, ref transformTRS.Rotation, ref transformTRS.Position,
                                        out localMatrix);
                                    matrix = Matrix.Multiply(localMatrix, matrix);
                                }

                                // Done evaluating, let's decompose matrix
                                TransformTRS transform;
                                matrix.Decompose(out transform.Scale, out transform.Rotation, out transform.Position);

                                // Create a key
                                translationCurve.KeyFrames.Add(new KeyFrameData<Vector3>(animationKey, transform.Position));
                                rotationCurve.KeyFrames.Add(new KeyFrameData<Quaternion>(animationKey, transform.Rotation));
                                scaleCurve.KeyFrames.Add(new KeyFrameData<Vector3>(animationKey, transform.Scale));
                            }

                            combinedAnimationClip.AddCurve($"{nameof(ModelNodeTransformation.Transform)}.{nameof(TransformTRS.Position)}", translationCurve);
                            combinedAnimationClip.AddCurve($"{nameof(ModelNodeTransformation.Transform)}.{nameof(TransformTRS.Rotation)}", rotationCurve);
                            combinedAnimationClip.AddCurve($"{nameof(ModelNodeTransformation.Transform)}.{nameof(TransformTRS.Scale)}", scaleCurve);
                            nodeAnimationClip = combinedAnimationClip;
                        }

                        var transformStart = $"{nameof(ModelNodeTransformation.Transform)}.";
                        var transformPosition = $"{nameof(ModelNodeTransformation.Transform)}.{nameof(TransformTRS.Position)}";

                        foreach (var channel in nodeAnimationClip.Channels)
                        {
                            var curve = nodeAnimationClip.Curves[channel.Value.CurveIndex];

                            // TODO: Root motion
                            var channelName = channel.Key;
                            if (channelName.StartsWith(transformStart))
                            {
                                if (channelName == transformPosition)
                                {
                                    // Translate node with parent 0 using PivotPosition
                                    var keyFrames = ((AnimationCurve<Vector3>)curve).KeyFrames;
                                    for (int i = 0; i < keyFrames.Count; ++i)
                                    {
                                        if (parentNodeIndex == 0)
                                            keyFrames.Items[i].Value -= PivotPosition;
                                        keyFrames.Items[i].Value *= ScaleImport;
                                    }
                                }
                                animationClip.AddCurve($"[ModelComponent.Key].Skeleton.NodeTransformations[{skeletonMapping.SourceToTarget[nodeIndex]}]." + channelName, curve);
                            }
                        }
                    }
                }

                if (ImportCustomAttributes)
                {
                    // Add clips clips animating other properties than node transformations
                    foreach (var nodeAnimationClipPair in animationClips)
                    {
                        var nodeName = nodeAnimationClipPair.Key;
                        var nodeAnimationClip = nodeAnimationClipPair.Value;

                        foreach (var channel in nodeAnimationClip.Channels)
                        {
                            var channelName = channel.Key;
                            var channelValue = channel.Value;
                            if (channelValue.IsUserCustomProperty)
                                animationClip.AddCurve(nodeName + "_" + channelName, nodeAnimationClip.Curves[channel.Value.CurveIndex], true);
                        }
                    }
                }
            }

            if (animationClip.Channels.Count == 0)
            {
                var logString = $"File {SourcePath} doesn't have any animation information.";

                if (failOnEmptyAnimation)
                {
                    commandContext.Logger.Error(logString);
                    return null;
                }

                commandContext.Logger.Info(logString);
            }
            else
            {
                if (animationClip.Duration.Ticks == 0)
                {
                    commandContext.Logger.Verbose($"File {SourcePath} has a 0 tick long animation.");
                }

                // Optimize and set common parameters
                animationClip.RepeatMode = AnimationRepeatMode;
                animationClip.Optimize();
            }
            return animationClip;
        }
    }
}
