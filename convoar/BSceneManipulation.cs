/*
 * Copyright (c) 2017 Robert Adams
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using OMV = OpenMetaverse;
using OMVR = OpenMetaverse.Rendering;

namespace org.herbal3d.convoar {

    // Representation of instances and whole scene information
    public class BSceneManipulation : IDisposable {
        private static string _logHeader = "[BSceneManipulation]";

        public BSceneManipulation() {
        }

        public void Dispose() {
        }

        public BScene OptimizeScene(BScene bScene) {
            List<BInstance> newInstances = new List<BInstance>();

            // Create collections of meshes with similar materials
            SceneAnalysis analysis = new SceneAnalysis(bScene);

            if (ConvOAR.Globals.parms.P<bool>("MergeSharedMaterialMeshes")) {
                newInstances.AddRange(MergeSharedMaterialMeshes(bScene));
            }
            if (ConvOAR.Globals.parms.P<bool>("SeparateInstancedMeshes")) {
            }

            // Replace the scene's instances with the new instances
            bScene.instances = newInstances;

            return bScene;
        }

        private class InvertedMesh {
            public BScene containingScene;
            public BInstance containingInstance;
            public Displayable containingDisplayable;
            public DisplayableRenderable containingDisplayableRenderable;
            public RenderableMesh renderableMesh;

            public InvertedMesh(BScene pBs, BInstance pInst, Displayable pDisp, DisplayableRenderable pDisprend, RenderableMesh pRm) {
                containingScene = pBs;
                containingInstance = pInst;
                containingDisplayable = pDisp;
                containingDisplayableRenderable = pDisprend;
                renderableMesh = pRm;
            }
        }

        private class SceneAnalysis {
            // meshes organized by the material they use
            Dictionary<BHash, List<InvertedMesh>> meshByMaterial = new Dictionary<BHash, List<InvertedMesh>>();
            // meshes organized by the mesh they share (for finding instances of identical mesh
            Dictionary<BHash, List<InvertedMesh>> sharedMeshes = new Dictionary<BHash, List<InvertedMesh>>();

            public SceneAnalysis() {
            }
            public SceneAnalysis(BScene bScene) {
                BuildAnalysis(bScene);
            }

            public void BuildAnalysis(BScene bScene) {
                foreach (BInstance inst in bScene.instances) {
                    MapMaterialsAndMeshes(bScene, inst, inst.Representation);
                }
            }

            // Given a list of meshes, remove them from the collection of meshes arranged by used materials.
            // This is used by other optimizations to remove meshes that have been optimized elsewhere.
            public void RemoveMeshesFromMaterials(List<InvertedMesh> meshList,
                                Dictionary<BHash, List<InvertedMesh>> meshByMaterial) {
                // Remove these meshes from the ones that are shared by material
                foreach (InvertedMesh imesh in meshList) {
                    BHash materialHash = imesh.renderableMesh.material.GetBHash();
                    if (!meshByMaterial[materialHash].Remove(imesh)) {
                        ConvOAR.Globals.log.DebugFormat("{0} CreateInstancesForSharedMeshes: couldn't remove imesh. matHash={1}",
                                _logHeader, materialHash);
                    }
                }
            }

            // Find all the meshes in passed Displayable and add them to the lists indexed by their material hashes
            // TODO: this only works for one level childing. For multi-level, must computer and pass the relative
            //      pos/rot as the layers of offsets are used. Change InvertedMesh to hold pos/rot.
            private void MapMaterialsAndMeshes(BScene pBs, BInstance pInst, Displayable disp) {
                RenderableMeshGroup rmg = disp.renderable as RenderableMeshGroup;
                if (rmg != null) {
                    foreach (RenderableMesh rMesh in rmg.meshes) {
                        InvertedMesh imesh = new InvertedMesh(pBs, pInst, disp, rmg, rMesh);

                        BHash meshHash = rMesh.mesh.GetBHash();
                        if (!sharedMeshes.ContainsKey(meshHash)) {
                            sharedMeshes.Add(meshHash, new List<InvertedMesh>());
                        }
                        sharedMeshes[meshHash].Add(imesh);

                        BHash materialHash = rMesh.material.GetBHash();
                        if (!meshByMaterial.ContainsKey(materialHash)) {
                            meshByMaterial.Add(materialHash, new List<InvertedMesh>());
                        }
                        meshByMaterial[materialHash].Add(imesh);
                    }
                }
                foreach (Displayable child in disp.children) {
                    MapMaterialsAndMeshes(pBs, pInst, child);
                }
            }
        }

        public List<BInstance> MergeSharedMaterialMeshes(BScene bScene) {

            List<BInstance> ret = new List<BInstance>();

            try {
                // 'analysis.meshByMaterial' has all meshes/instances grouped by material used
                // 'analysis.sharedMeshes' has all meshes grouped by the mesh
                // If there are lots of instances of the same mesh, it is better to have multiple instances
                //    that point to the same mesh. If a mesh is not shared, consolidating the meshes
                //    into a single instance is best. It's a balance of transferring vertices vs fewer draws.

                // Any meshes that are used more than 'MeshShareThreshold' will be sent out with their
                //    instances rather than being combined.
                // The GLTF output code will not send out duplicate meshes and combining the meshes to
                //    share materials destroys the duplicatable mesh shapes.
                // The duplicated meshes usually share a material so pull them together into meshes
                //    in one instance.
                // Note: the 'SelectMany' is used to flatten the list of lists
                int meshShareThreshold = ConvOAR.Globals.parms.P<int>("MeshShareThreshold");
                if (ConvOAR.Globals.parms.P<bool>("SeparateInstancedMeshes")) {
                    ret.AddRange(sharedMeshes.Values.Where(val => val.Count > meshShareThreshold).SelectMany(meshList => {
                        // Creates Instances for the shared messes in this list and also takes the meshes out of 'meshByMaterial'
                        ConvOAR.Globals.log.DebugFormat("{0}: MergeSharedMaterialMeshes: shared mesh hash: {1}/{2}, cnt={3}",
                                _logHeader, meshList.First().renderableMesh.mesh.GetBHash(),
                                meshList.First().renderableMesh.material.GetBHash(),
                                meshList.Count);
                        RemoveMeshesFromMaterials(meshList, meshByMaterial);
                        return CreateInstancesForSharedMeshes(meshList);
                    }).ToList() );
                }

                ConvOAR.Globals.log.DebugFormat("{0} MergeShareMaterialHashes: number of materials = {1}",
                                    _logHeader, meshByMaterial.Count);

                // Merge the meshes and create an Instance containing the new mesh set
                ret.AddRange(meshByMaterial.Keys.SelectMany(materialHash => {
                    ConvOAR.Globals.log.DebugFormat("{0} MergeShareMaterialHashes: material hash {1} . meshes={2}",
                                _logHeader, materialHash, meshByMaterial[materialHash].Count);
                    return CreateInstancesFromSharedMaterialMeshes(materialHash, meshByMaterial[materialHash]);
                }).ToList() );
            }
            catch (Exception e) {
                ConvOAR.Globals.log.DebugFormat("{0} MergeShareMaterialHashes: exception: {1}", _logHeader, e);
            }

            return ret;
        }

        // Create one or more Instances from this list of meshes.
        // There might be more than 2^15 vertices so, to keep the indices a ushort, might need
        //    to break of the meshes.
        private List<BInstance> CreateInstancesFromSharedMaterialMeshes(BHash materialHash, List<InvertedMesh> meshes) {
            List<BInstance> ret = new List<BInstance>();

            List<InvertedMesh> partial = new List<InvertedMesh>();
            int totalVertices = 0;
            foreach (InvertedMesh imesh in meshes) {
                if (totalVertices + imesh.renderableMesh.mesh.vertexs.Count > 50000) {
                    // if adding this mesh will push us over the max, create instances and start again
                    ret.Add(CreateOneInstanceFromMeshes(materialHash, partial));
                    partial.Clear();
                    totalVertices = 0;
                }
                totalVertices += imesh.renderableMesh.mesh.vertexs.Count;
                partial.Add(imesh);
            }
            if (partial.Count > 0) {
                ret.Add(CreateOneInstanceFromMeshes(materialHash, partial));
            }

            return ret;
        }

        // Given a list of meshes, combine them into one mesh and return a containing BInstance.
        private BInstance CreateOneInstanceFromMeshes(BHash materialHash, List<InvertedMesh> meshes) {
            // Pick one of the meshes to be the 'root' mesh.
            // Someday may need to find the most center mesh to work from.
            InvertedMesh rootIMesh = meshes.First();

            // The new instance will be at the location of the root mesh with no rotation
            BInstance inst = new BInstance();
            inst.Position = rootIMesh.containingInstance.Position;
            inst.Rotation = OMV.Quaternion.Identity;
            inst.coordAxis = rootIMesh.containingInstance.coordAxis;

            try {
                // The mesh we're going to build
                MeshInfo meshInfo = new MeshInfo();
                foreach (InvertedMesh imesh in meshes) {
                    int indicesBase = meshInfo.vertexs.Count;
                    // Go through the mesh, map all vertices to global coordinates then convert relative to root
                    meshInfo.vertexs.AddRange(imesh.renderableMesh.mesh.vertexs.Select(vert => {
                        OMVR.Vertex newVert = new OMVR.Vertex();
                        OMV.Vector3 worldPos = vert.Position;
                        worldPos = worldPos * imesh.containingDisplayable.offsetRotation
                                + imesh.containingDisplayable.offsetPosition;
                        worldPos = worldPos * imesh.containingInstance.Rotation
                                + imesh.containingInstance.Position;
                        // Make new vert relative to the BInstance it's being added to
                        newVert.Position = worldPos - inst.Position;
                        newVert.Normal = vert.Normal
                            * imesh.containingDisplayable.offsetRotation
                            * imesh.containingInstance.Rotation;
                        newVert.TexCoord = vert.TexCoord;
                        return newVert;
                    }));
                    meshInfo.indices.AddRange(imesh.renderableMesh.mesh.indices.Select(ind => ind + indicesBase));
                }

                RenderableMesh newMesh = new RenderableMesh();
                newMesh.num = 0;
                newMesh.material = rootIMesh.renderableMesh.material;   // The material we share
                newMesh.mesh = meshInfo;

                RenderableMeshGroup meshGroup = new RenderableMeshGroup();
                meshGroup.meshes.Add(newMesh);

                Displayable displayable = new Displayable(meshGroup);
                displayable.name = "combinedMaterialMeshes-" + materialHash.ToString();

                inst.Representation = displayable;
            }
            catch (Exception e) {
                ConvOAR.Globals.log.ErrorFormat("{0} CreateInstanceFromSharedMaterialMeshes: exception: {1}", _logHeader, e);
            }

            return inst;
        }

        // Creates Instances for the shared messes in this list and also takes the meshes out of 'meshByMaterial'.
        // Current algorithm: create one instance and add all shared meshes as children.
        // When the instances are created (or copied over), the meshes must be removed from the
        //     'meshByMaterial' structure so they are not combined with other material sharing meshes.
        private List<BInstance> CreateInstancesForSharedMeshes(List<InvertedMesh> meshList) {
            List<BInstance> ret = new List<BInstance>();

            // Create an instance for each identical mesh (so we can keep the pos and rot.
            ret.AddRange(meshList.Select(imesh => {
                /*
                ConvOAR.Globals.log.DebugFormat("{0} CreateInstanceForSharedMeshes: hash={1}, instPos={2}, dispPos={3}, numVerts={4}",
                                _logHeader, imesh.renderableMesh.mesh.GetBHash(),
                                imesh.containingInstance.Position,
                                imesh.containingDisplayable.offsetPosition,
                                imesh.renderableMesh.mesh.vertexs.Count);
                */

                RenderableMeshGroup mesh = new RenderableMeshGroup();
                mesh.meshes.Add(imesh.renderableMesh);

                Displayable disp = new Displayable(mesh);
                disp.name = "sharedMesh-" + imesh.renderableMesh.mesh.GetBHash().ToString() + "-" + imesh.renderableMesh.GetBHash().ToString();
                disp.offsetPosition = imesh.containingDisplayable.offsetPosition;
                disp.offsetRotation = imesh.containingDisplayable.offsetRotation;

                BInstance inst = new BInstance();
                inst.Position = imesh.containingInstance.Position;
                inst.Rotation = imesh.containingInstance.Rotation;
                inst.Representation = disp;
                return inst;
            }) );

            return ret;
        }

    }
}