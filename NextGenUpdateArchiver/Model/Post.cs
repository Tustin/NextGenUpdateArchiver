using System;
using System.Collections.Generic;

namespace NextGenUpdateArchiver.Model
{
    public class Post
    {
        public string Contents { get; set; }
        public List<int> Thanks { get; set; }
        public DateTime PostDate { get; set; }
    }
}