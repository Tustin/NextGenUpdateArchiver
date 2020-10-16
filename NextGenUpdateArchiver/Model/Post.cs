using System;
using System.Collections.Generic;

namespace NextGenUpdateArchiver.Model
{
    public class Post
    {
        public int Id { get; set; }
        public string Poster { get; set; }
        public string Contents { get; set; }
        public List<string> Thanks { get; set; } = new List<string>();
        public DateTime PostDate { get; set; }
    }
}