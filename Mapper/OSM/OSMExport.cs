﻿using ColossalFramework;
using ColossalFramework.Math;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using UnityEngine;
using Mapper.Contours;

namespace Mapper.OSM
{
    public class OSMExport
    {
        public RoadMapping mapping;
        private int nodeCount;
        private int wayCount;
        private List<OSMNode> nodes;
        private List<OSMWay> ways;
        private Vector3 middle;
        private float buildingCount;

        internal void Export()
        {
            Debug.Log("Beginning export");
            mapping = new RoadMapping(4.5);
            var osm = new OSM();
            osm.version = 0.6M;
            osm.meta = new osmMeta { osm_base = DateTime.Now };
            osm.generator = "Cities Skylines Magic Mapper Mod";
            osm.note = Singleton<SimulationManager>.instance.m_metaData.m_CityName;
            osm.bounds = new osmBounds { minlon = 35.753054M, minlat = 34.360353M, maxlon = 35.949310M, maxlat = 34.522050M };
            var nm = Singleton<NetManager>.instance;

            mapping.InitBoundingBox(osm.bounds, 1);

            nodeCount = 128000;
            wayCount = 128000;

            AddNodesAndWays();
            AddBuildings();
            AddDistricts();
            AddCity();
            AddCountours();
            AddRoutes();

            mapping.printDebug();

            osm.node = FilterUnusedNodes();
            osm.way = ways.ToArray();

            var serializer = new XmlSerializer(typeof(OSM));
            var ms = new StreamWriter(Singleton<SimulationManager>.instance.m_metaData.m_CityName + ".osm");
            serializer.Serialize(ms, osm);
            ms.Close();
        }

        private void AddCountours()
        {
            var gridSize = 16;
            var steps = (1920 * 9 / gridSize);
            var data = new double[steps + 2, steps + 2];

            var x = new double[steps + 2];
            var y = new double[steps + 2];

            for (var i = 0; i < steps + 2; i += 1)
            {
                for (var j = 0; j < steps + 2; j += 1)
                {
                    if (i == 0 || i == steps + 1 || j == 0 || j == steps + 1)
                    {
                        data[i, j] = 0;
                    }
                    else
                    {
                        var pos = new Vector3((i - 1 - steps / 2) * gridSize, 0, (j - 1 - steps / 2) * gridSize);
                        var waterLevel = Singleton<TerrainManager>.instance.SampleRawHeightSmoothWithWater(pos, false, 0f);
                        var groundLevel = Singleton<TerrainManager>.instance.SampleRawHeightSmooth(pos);
                        data[i, j] = waterLevel - groundLevel;
                    }
                }
                x[i] = i * gridSize;
                y[i] = i * gridSize;
            }

            var z = new double[] { 1.6 };
            var result = new Dictionary<Vector2, List<Vector2>>[z.Length];
            for (var i = 0; i < z.Length; i += 1)
            {
                result[i] = new Dictionary<Vector2, List<Vector2>>();
            }

            Conrec.Contour(data, x, y, z, result);

            var chains = Process(result[0]);
            chains = Simplify(chains);

            foreach (var chain in chains)
            {
                if (chain.Count == 0)
                {
                    continue;
                }
                var nds = new List<osmWayND>();
                foreach (var node in chain)
                {
                    nds.Add(new osmWayND { @ref = AddNode(new Vector3(node.x - gridSize - steps * gridSize / 2, 0, node.y - gridSize - steps * gridSize / 2)) });
                }
                nds.Add(new osmWayND { @ref = nds[0].@ref });
                var tags = new List<osmWayTag>();
                tags.Add(new osmWayTag { k = "natural", v = "water" });

                wayCount += 1;
                ways.Add(new OSMWay { changeset = 50000000, id = (uint)wayCount, timestamp = DateTime.Now, user = "CS", version = 1, nd = nds.ToArray(), tag = tags.ToArray() });
            }
        }

        private List<List<Vector2>> Simplify(List<List<Vector2>> chains)
        {
            var fc = new Mapper.Curves.FitCurves();
            var result = new List<List<Vector2>>();
            foreach (var chain in chains)
            {
                var temp = Mapper.Curves.Douglas.DouglasPeuckerReduction(chain, 8.0);
                if (temp != null && temp.Count > 1)
                {
                    result.Add(temp);
                    //result.Add(BezierToPoints(fc.FitCurve(temp.ToArray(), 10.0)));
                }
            }
            return result;
        }

        private List<Vector2> BezierToPoints(List<Segment> list)
        {
            var result = new List<Vector2>();
            result.Add(list[0].startPoint);
            foreach (var seg in list)
            {
                if (seg.controlA == Vector2.zero)
                {
                    result.Add(seg.endPoint);
                }
                else
                {
                    var bezier = new Bezier3(seg.startPoint, seg.controlA, seg.controlB, seg.endPoint);
                    var steps = Mathf.RoundToInt((seg.endPoint - seg.startPoint).magnitude / 50) + 1;
                    for (var i = 0; i <= steps; i += 1)
                    {
                        if (i == 0)
                        {
                            continue;
                        }
                        result.Add(bezier.Position(i / (float)steps));
                    }
                }
            }
            return result;
        }

        public static List<List<Vector2>> Process(Dictionary<Vector2, List<Vector2>> list)
        {
            var finalChains = new List<List<Vector2>>();
            var chainStarts = new Dictionary<Vector2, List<Vector2>>();
            var chainEnds = new Dictionary<Vector2, List<Vector2>>();

            foreach (var item in list)
            {
                foreach (var end in item.Value)
                {
                    var start = item.Key;

                    var foundStart = chainStarts.ContainsKey(start);
                    var foundStart2 = chainEnds.ContainsKey(start);

                    var foundEnd = chainStarts.ContainsKey(end);
                    var foundEnd2 = chainEnds.ContainsKey(end);

                    if (!foundStart && !foundEnd && !foundStart2 && !foundEnd2)
                    {
                        var newChain = new List<Vector2>();
                        chainStarts.Add(start, newChain);
                        chainEnds.Add(end, newChain);
                        newChain.Add(start);
                        newChain.Add(end);
                        //chains.Add(newChain);
                    }
                    else
                    {
                        List<Vector2> cs = null;
                        List<Vector2> ce = null;
                        if (foundStart)
                        {
                            cs = chainStarts[start];
                            chainStarts.Remove(start);
                        }
                        else if (foundStart2)
                        {
                            cs = chainEnds[start];
                            chainEnds.Remove(start);
                        }

                        if (foundEnd)
                        {
                            ce = chainStarts[end];
                            chainStarts.Remove(end);
                        }
                        else if (foundEnd2)
                        {
                            ce = chainEnds[end];
                            chainEnds.Remove(end);
                        }

                        if (cs != null && ce != null)
                        {
                            if (cs.Equals(ce))
                            {
                                finalChains.Add(cs);
                                continue;
                            }

                            //chains.Remove(ce);
                            if (foundEnd)
                            {
                                chainEnds.Remove(ce[ce.Count() - 1]);
                                if (foundStart)
                                {
                                    chainStarts.Add(ce[ce.Count() - 1], cs);
                                    JoinChains(cs, ce, true, cs, false);
                                }
                                else
                                {
                                    chainEnds.Add(ce[ce.Count() - 1], cs);
                                    JoinChains(cs, cs, false, ce, false);
                                }
                            }
                            else
                            {
                                chainStarts.Remove(ce[0]);
                                if (foundStart)
                                {
                                    chainStarts.Add(ce[0], cs);
                                    JoinChains(cs, ce, false, cs, false);
                                }
                                else
                                {
                                    chainEnds.Add(ce[0], cs);
                                    JoinChains(cs, cs, false, ce, true);
                                }
                            }
                        }
                        else
                        {
                            if (foundStart)
                            {
                                cs.Insert(0, end);
                                chainStarts.Add(end, cs);
                            }
                            else if (foundStart2)
                            {
                                cs.Add(end);
                                chainEnds.Add(end, cs);
                            }
                            if (foundEnd)
                            {
                                ce.Insert(0, start);
                                chainStarts.Add(start, ce);
                            }
                            else if (foundEnd2)
                            {
                                ce.Add(start);
                                chainEnds.Add(start, ce);
                            }
                        }
                    }
                }
            }
            return finalChains;
        }

        private static void JoinChains(List<Vector2> final, List<Vector2> start, bool flipStart, List<Vector2> end, bool flipEnd)
        {
            var result = new List<Vector2>();
            for (var i = 0; i < start.Count; i += 1)
            {
                if (flipStart)
                {
                    result.Add(start[start.Count - 1 - i]);
                }
                else
                {
                    result.Add(start[i]);
                }
            }

            for (var i = 0; i < end.Count; i += 1)
            {
                if (flipEnd)
                {
                    result.Add(end[end.Count - 1 - i]);
                }
                else
                {
                    result.Add(end[i]);
                }
            }

            final.Clear();
            foreach (var res in result)
            {
                final.Add(res);
            }
        }

        private void AddBuildings()
        {
            var bm = Singleton<BuildingManager>.instance;
            for (var i = 0; i < 32768; i += 1)
            {
                var building = bm.m_buildings.m_buffer[i];
                if ((building.m_flags & Building.Flags.Created) != Building.Flags.None)
                {
                    var way = AddBuilding((ushort)i, building);
                    if (way != null)
                    {
                        ways.Add(way);
                    }
                }
            }
        }

        private OSMWay AddBuilding(ushort buildingId, Building data)
        {
            int width = data.Width;
            int length = data.Length;
            Vector3 a = new Vector3(Mathf.Cos(data.m_angle), 0f, Mathf.Sin(data.m_angle)) * 8f;
            Vector3 a2 = new Vector3(a.z, 0f, -a.x);


            var tags = new List<osmWayTag>();

            string amenity = "";
            mapping.GetTags(buildingId, data, tags, ref amenity);

            wayCount += 1;
            if (tags.Count == 0)
            {
                return null;
            }

            this.middle += data.m_position;
            this.buildingCount += 1;

            osmWayND[] nd = new osmWayND[5];
            var firstNode = AddNode(data.m_position - (float)width * 0.5f * a - (float)length * 0.5f * a2);
            nd[0] = new osmWayND { @ref = firstNode };
            nd[1] = new osmWayND { @ref = AddNode(data.m_position + (float)width * 0.5f * a - (float)length * 0.5f * a2) };
            nd[2] = new osmWayND { @ref = AddNode(data.m_position + (float)width * 0.5f * a + (float)length * 0.5f * a2) };
            nd[3] = new osmWayND { @ref = AddNode(data.m_position - (float)width * 0.5f * a + (float)length * 0.5f * a2) };
            nd[4] = new osmWayND { @ref = firstNode };

            if (amenity != "")
            {
                var ammenityTag = new Dictionary<string, string>();
                ammenityTag.Add("amenity", amenity);
                AddNode(data.m_position, ammenityTag);
            }
            return new OSMWay { changeset = 50000000, id = (uint)wayCount, timestamp = DateTime.Now, user = "CS", version = 1, nd = nd, tag = tags.ToArray() };
        }

        private void AddCity()
        {
            Debug.Log("Building count: " + this.buildingCount.ToString());
            Debug.Log("Middle: " + this.middle.ToString());
            decimal lon = 0.0M;
            decimal lat = 0.0M;
            mapping.GetPos(new Vector3(this.middle.x / this.buildingCount, 0, this.middle.y / this.buildingCount), out lon, out lat);
            var md = Singleton<SimulationManager>.instance;
            var tags = new List<osmNodeTag>();
            tags.Add(new osmNodeTag { k = "name", v = md.m_metaData.m_CityName });
            tags.Add(new osmNodeTag { k = "place", v = "city" });
            tags.Add(new osmNodeTag { k = "population", v = Singleton<DistrictManager>.instance.m_districts.m_buffer[0].m_populationData.m_finalCount.ToString() });

            nodes.Add(new OSMNode { changeset = 50000000, id = (uint)nodeCount, version = 1, timestamp = DateTime.Now, user = "CS", lon = lon, lat = lat, tag = tags.ToArray() });
        }

        private void AddDistricts()
        {
            var dm = Singleton<DistrictManager>.instance;
            for (var i = 1; i < 128; i += 1)
            {
                if ((dm.m_districts.m_buffer[i].m_flags & District.Flags.Created) != District.Flags.None)
                {
                    AddDistrict(dm.GetDistrictName(i), "suburb", dm.m_districts.m_buffer[i].m_nameLocation);
                }
            }
        }

        private void AddDistrict(string name, string place, Vector3 vector3)
        {
            decimal lon = 0;
            decimal lat = 0;
            mapping.GetPos(vector3, out lon, out lat);
            var tags = new List<osmNodeTag>();
            tags.Add(new osmNodeTag { k = "name", v = name });
            tags.Add(new osmNodeTag { k = "place", v = place });

            nodes.Add(new OSMNode { changeset = 50000000, id = (uint)nodeCount, version = 1, timestamp = DateTime.Now, user = "CS", lon = lon, lat = lat, tag = tags.ToArray() });
            nodeCount += 1;
        }

        private OSMNode[] FilterUnusedNodes()
        {

            var found = new HashSet<uint>();
            foreach (var way in ways)
            {
                foreach (var nd in way.nd)
                {
                    found.Add(nd.@ref);
                }
            }

            var finalNodes = new List<OSMNode>();
            foreach (var node in nodes)
            {
                if (node.tag != null || found.Contains(node.id))
                {
                    finalNodes.Add(node);
                }
            }
            return finalNodes.ToArray();
        }

        private void AddNodesAndWays()
        {
            var nm = Singleton<NetManager>.instance;
            nodes = new List<OSMNode>();
            for (var i = 0; i < 32768u; i += 1)
            {
                if ((nm.m_nodes.m_buffer[i].m_flags & NetNode.Flags.Created) != NetNode.Flags.None)
                {
                    nodes.Add(AddNode(i, nm.m_nodes.m_buffer[i]));
                }
            }

            ways = new List<OSMWay>();
            for (var i = 0; i < 32768u; i += 1)
            {
                if ((nm.m_segments.m_buffer[i].m_flags & NetSegment.Flags.Created) != NetSegment.Flags.None)
                {
                    var result = AddWay(i, nm.m_segments.m_buffer[i]);
                    if (result != null)
                    {
                        ways.Add(result);
                    }
                }
            }
        }

        private OSMWay AddWay(int i, NetSegment netSegment)
        {
            var nm = Singleton<NetManager>.instance;
            if (netSegment.m_startNode == 0 || netSegment.m_endNode == 0)
            {
                return null;
            }

            var startNode = netSegment.m_startNode;
            var endNode = netSegment.m_endNode;
            var startDirection = netSegment.m_startDirection;
            var endDirection = netSegment.m_endDirection;

            if ((netSegment.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None)
            {
                startNode = netSegment.m_endNode;
                endNode = netSegment.m_startNode;
                startDirection = netSegment.m_endDirection;
                endDirection = netSegment.m_startDirection;
            }
            OSMWayND[] nd;
            if (Vector3.Angle(startDirection, -endDirection) > 3f)
            {
                var start = nm.m_nodes.m_buffer[startNode].m_position;
                var end = nm.m_nodes.m_buffer[endNode].m_position;
                Vector3 a = Vector3.zero;
                Vector3 b = Vector3.zero;
                NetSegment.CalculateMiddlePoints(start, startDirection, end, endDirection, false, false, out a, out b);

                var bezier = new Bezier3(start, a, b, end);

                nd = new osmWayND[5];
                nd[0] = new osmWayND { @ref = startNode };
                nd[1] = new osmWayND { @ref = AddNode(bezier.Position(0.25f)) };
                nd[2] = new osmWayND { @ref = AddNode(bezier.Position(0.5f)) };
                nd[3] = new osmWayND { @ref = AddNode(bezier.Position(0.75f)) };
                nd[4] = new osmWayND { @ref = endNode };
            }
            else
            {
                nd = new osmWayND[2];
                nd[0] = new osmWayND { @ref = startNode };
                nd[1] = new osmWayND { @ref = endNode };
            }

            var startBuffer = nm.m_nodes.m_buffer[startNode];
            var endBuffer = nm.m_nodes.m_buffer[endNode];

            byte elevation = (byte)(Mathf.Clamp((startBuffer.m_elevation + endBuffer.m_elevation) / 2, 0, 255));
            bool underground = startBuffer.m_flags.IsFlagSet(NetNode.Flags.Underground) || endBuffer.m_flags.IsFlagSet(NetNode.Flags.Underground);

            var tags = new List<osmWayTag>();
            if (!mapping.GetTags(elevation, netSegment, tags, underground))
            {
                return null;
            }
            return new OSMWay { changeset = 50000000, id = (uint)i, timestamp = DateTime.Now, user = "CS", version = 1, nd = nd, tag = tags.ToArray() };
        }

        private uint AddNode(Vector3 vector3, Dictionary<string, string> tags = null)
        {
            var node = AddNode(nodeCount, vector3);
            if (tags != null)
            {
                var ost = new List<osmNodeTag>();
                foreach (var kvp in tags)
                {
                    ost.Add(new osmNodeTag { k = kvp.Key, v = kvp.Value });
                }
                node.tag = ost.ToArray();
            }
            nodes.Add(node);
            nodeCount += 1;
            return (uint)(nodeCount - 1);
        }

        private OSMNode AddNode(int i, NetNode netNode)
        {
            return AddNode(i, netNode.m_position);
        }

        private OSMNode AddNode(int i, Vector3 vector3)
        {
            decimal lon = 0;
            decimal lat = 0;
            mapping.GetPos(vector3, out lon, out lat);
            return new OSMNode { changeset = 50000000, id = (uint)i, version = 1, timestamp = DateTime.Now, user = "CS", lon = lon, lat = lat, };
        }

        private void AddRoutes()
        {
            var transportLines = Singleton<TransportManager>.instance;

            foreach (TransportLine line in transportLines.m_lines.m_buffer)
            {
                int numberOfStops = line.CountStops(line.m_stops);
                var transportType = line.Info.m_transportType;

                if (line.m_stops != 0 && numberOfStops > 0)
                {
                    for (int stopIndex = 0; stopIndex < numberOfStops; ++stopIndex)
                    {
                        ushort stopId = line.GetStop(stopIndex);
                        NetNode stop = Singleton<NetManager>.instance.m_nodes.m_buffer[(int)stopId];
                        Vector3 position = stop.m_position;
                        ushort transportLine = stop.m_transportLine;
                        string transportLineName = transportLineName = transportLines.GetLineName(transportLine);;

                        decimal lon = 0;
                        decimal lat = 0;
                        mapping.GetPos(position, out lon, out lat);

                        var tags = new List<osmNodeTag>();

                        if (transportType == TransportInfo.TransportType.Bus)
                        {
                            tags.Add(new osmNodeTag { k = "highway", v = "bus_stop" });
                        }
                        else if (transportType == TransportInfo.TransportType.Train)
                        {
                            bool tramLine = transportLineName != null && (transportLineName.Contains("[t]") || transportLineName.ToLower().Contains("tram"));
                            tags.Add(new osmNodeTag { k = "public_transport", v = "platform" });
                            tags.Add(new osmNodeTag { k = "railway", v = tramLine ? "tram_stop" : "station" });
                        }

                        nodes.Add(new OSMNode { changeset = 50000000, id = (uint)nodeCount, version = 1, timestamp = DateTime.Now, user = "CS", lon = lon, lat = lat, tag = tags.ToArray() });
                        nodeCount += 1;
                    }
                }
            }
        }
    }
}
