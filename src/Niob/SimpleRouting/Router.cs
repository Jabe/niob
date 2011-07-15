using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Niob.SimpleRouting
{
    public class Router : IEnumerable<SimpleRoute>
    {
        private static readonly Regex PathExtractor = new Regex(@"^[^/:]+://[^/]+(?<p>/[^?]*)");

        private readonly List<SimpleRoute> _routes = new List<SimpleRoute>();

        #region IEnumerable<SimpleRoute> Members

        public IEnumerator<SimpleRoute> GetEnumerator()
        {
            return _routes.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        #endregion

        public void Add(string name, string route)
        {
            Add(new SimpleRoute(name, route));
        }

        public void Add(SimpleRoute route)
        {
            lock (_routes)
            {
                _routes.Add(route);
            }
        }

        public RouteMatch GetFirstHit(string url)
        {
            return GetFirstHit(new Uri(url));
        }

        public RouteMatch GetFirstHit(Uri url)
        {
            lock (_routes)
            {
                foreach (SimpleRoute route in _routes)
                {
                    Match pathMatches = PathExtractor.Match(url.OriginalString);

                    if (!pathMatches.Success)
                        continue;

                    string path = pathMatches.Groups["p"].Value;

                    Regex regex = route.ParsedRoute;
                    Match rmatch = regex.Match(path);

                    if (!rmatch.Success)
                        continue;

                    var match = new RouteMatch(url, route);

                    for (int i = 1; i < rmatch.Groups.Count; i++)
                    {
                        Group @group = rmatch.Groups[i];

                        if (!@group.Success)
                            break;

                        string name = regex.GroupNameFromNumber(i);
                        string value = @group.Value;

                        // remove starting slash (for more readable regex)
                        value = value.Remove(0, 1);

                        match.Values.Add(name, value);
                    }

                    return match;
                }

                throw new Exception("No matching route found.");
            }
        }
    }
}