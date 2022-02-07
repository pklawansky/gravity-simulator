using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GravitySimulator.Models.Engine3D
{
    public class mesh
    {
        List<triangle> tris;

        bool LoadFromObjectFile(string sFilename, bool bHasTexture = false)
        {
            using (var f = new FileStream(sFilename, FileMode.Open))
            {
                if (!f.CanRead)
                    return false;

                // Local cache of verts
                List<vec3d> verts = new List<vec3d>();
                List<vec2d> texs = new List<vec2d>();

                byte[] fileData = new byte[f.Length];

                f.Read(fileData, 0, fileData.Length);

                string fileString = Encoding.ASCII.GetString(fileData);
                var lines = fileString.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    var s = line.Split(new string[] { " " }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();

                    if (line[0] == '#') continue;

                    if (line[0] == 'v')
                    {
                        if (line[1] == 't')
                        {
                            vec2d v = new vec2d();
                            v.u = float.Parse(s[2]);
                            v.v = float.Parse(s[3]);
                            // A little hack for the spyro texture
                            //v.u = 1.0f - v.u;
                            //v.v = 1.0f - v.v;
                            texs.Add(v);
                        }
                        else
                        {
                            vec3d v = new vec3d();
                            v.x = float.Parse(s[1]);
                            v.y = float.Parse(s[2]);
                            v.z = float.Parse(s[3]);
                            verts.Add(v);
                        }
                    }

                    if (!bHasTexture)
                    {
                        if (line[0] == 'f')
                        {
                            int[] ft = new int[3];
                            ft[0] = int.Parse(s[1]);
                            ft[1] = int.Parse(s[2]);
                            ft[2] = int.Parse(s[3]);
                            tris.Add(new triangle
                            {
                                p = new vec3d[3] { verts[ft[0] - 1], verts[ft[1] - 1], verts[ft[2] - 1] }
                            });
                        }
                    }
                    //else // not sure how this works yet and not sure what it is doing well enough to convert to C# yet
                    //{
                    //    if (line[0] == 'f')
                    //    {
                    //        s >> junk;

                    //        string tokens[6];
                    //        int nTokenCount = -1;


                    //        while (!s.eof())
                    //        {
                    //            char c = s.get();
                    //            if (c == ' ' || c == '/')
                    //                nTokenCount++;
                    //            else
                    //                tokens[nTokenCount].append(1, c);
                    //        }

                    //        tokens[nTokenCount].pop_back();


                    //        tris.push_back({
                    //            verts[stoi(tokens[0]) - 1], verts[stoi(tokens[2]) - 1], verts[stoi(tokens[4]) - 1],
                    //texs[stoi(tokens[1]) - 1], texs[stoi(tokens[3]) - 1], texs[stoi(tokens[5]) - 1] });

                    //    }

                    //}
                }
                return true;
            }
        }

    }
}
