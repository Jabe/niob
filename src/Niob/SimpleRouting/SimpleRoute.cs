using System;
using System.Text.RegularExpressions;

namespace Niob.SimpleRouting
{
    public class SimpleRoute
    {
        private static readonly Regex RouteConverter = new Regex(@"/\\\{([\w\d-_]+)\}", RegexOptions.IgnoreCase);

        public SimpleRoute(string name, string rawRoute)
        {
            Name = name;
            RawRoute = rawRoute;
            ParsedRoute = PrepareRoute(rawRoute);
        }

        public string Name { get; private set; }
        public string RawRoute { get; private set; }
        public Regex ParsedRoute { get; private set; }

        private Regex PrepareRoute(string route)
        {
            if (route == "*") return new Regex(".*");

            route = Regex.Escape(route);
            string routeRegex = RouteConverter.Replace(route, @"(?<$1>/[^/]*)?");

            return new Regex("^" + routeRegex + "$", RegexOptions.IgnoreCase);
        }

        public override string ToString()
        {
            return string.Format("{0} [{1}]", Name, RawRoute);
        }
    }
}