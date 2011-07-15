using System;
using System.Collections.Generic;

namespace Niob.SimpleRouting
{
    public class RouteMatch
    {
        public RouteMatch(Uri url, SimpleRoute route)
        {
            Url = url;
            Route = route;
            Values = new Dictionary<string, string>();
        }

        public Uri Url { get; private set; }
        public SimpleRoute Route { get; private set; }
        public IDictionary<string, string> Values { get; private set; }

        public override string ToString()
        {
            string str = string.Format("Url [{0}] matched [{1}]", Url, Route);

            foreach (var value in Values)
            {
                str += Environment.NewLine;
                str += "  Value: ";
                str += string.Format("[{0},{1}]", value.Key, value.Value);
            }

            return str;
        }
    }
}