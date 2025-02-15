using System;
using System.Text.Json.Serialization;
using LatokenHackaton.ASL;
using LatokenHackaton.Common;

namespace LatokenHackaton.Api.News
{
	public class NewsArticle
	{
        public DateTime DateTime { get; }
        public string Title { get; }
        public string Body { get; }

        [AslIgnore, ReadableSerializer.Ignore]
        public string Id { get; }

        [AslIgnore]
        public string Link { get; }

        public NewsArticle(
            DateTime dateTime,
            string title,
            string body,
            string id,
            string link
        )
        {
            this.DateTime = dateTime;
            this.Title = title;
            this.Body = body;
            this.Id = id;
            this.Link = link;
        }

        public override string ToString()
        {
            return $"({this.Id}) {this.Title} - {this.DateTime:f}\r\n\r\n{this.Body}\r\n\r\nLink: {this.Link}";
        }
    }
}

