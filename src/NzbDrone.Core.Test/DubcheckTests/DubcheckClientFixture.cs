using System;
using System.Collections.Generic;
using System.Net;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Dubcheck;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.DubcheckTests
{
    [TestFixture]
    public class DubcheckClientFixture : CoreTest<DubcheckClient>
    {
        private Movie _movie;

        [SetUp]
        public void Setup()
        {
            Environment.SetEnvironmentVariable("DUBCHECK_URL", "http://dubcheck:9395");
            _movie = Builder<Movie>.CreateNew()
                .With(m => m.QualityProfile = DubProfile("Hebrew", 20000))
                .With(m => m.MovieMetadata = new MovieMetadata { Title = "Frozen", TmdbId = 109445, OriginalLanguage = Language.English, Genres = new List<string> { "Animation" } })
                .Build();
        }

        private static QualityProfile DubProfile(string name, int score)
        {
            var format = new CustomFormat("Hebrew Audio (dubcheck)", new DubcheckSpecification { Value = Language.Hebrew.Id, Verdict = (int)DubVerdict.Has });
            return new QualityProfile
            {
                Name = name,
                FormatItems = new List<ProfileFormatItem> { new ProfileFormatItem { Format = format, Score = score } }
            };
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("DUBCHECK_URL", null);
        }

        private void GivenResponse(string json)
        {
            Mocker.GetMock<IHttpClient>()
                .Setup(c => c.Execute(It.IsAny<HttpRequest>()))
                .Returns<HttpRequest>(r => new HttpResponse(r, new HttpHeader(), json));
        }

        private ReleaseInfo Release(string guid = "g1") => new ReleaseInfo { Guid = guid, Title = "Frozen.2013.1080p", Description = "d" };

        [Test]
        public void should_return_verdict_from_dubcheck()
        {
            GivenResponse("{\"verdict\":\"has\",\"evidence\":[],\"cached\":false}");

            Subject.Check(Release(), _movie, Language.Hebrew).Should().Be(DubVerdict.Has);
        }

        [Test]
        public void should_send_profile_language_and_description()
        {
            HttpRequest sent = null;
            Mocker.GetMock<IHttpClient>()
                .Setup(c => c.Execute(It.IsAny<HttpRequest>()))
                .Callback<HttpRequest>(r => sent = r)
                .Returns<HttpRequest>(r => new HttpResponse(r, new HttpHeader(), "{\"verdict\":\"lacks\"}"));

            Subject.Check(Release(), _movie, Language.Hebrew).Should().Be(DubVerdict.Lacks);

            var body = sent.ContentSummary ?? System.Text.Encoding.UTF8.GetString(sent.ContentData);
            sent.Url.FullUri.Should().Be("http://dubcheck:9395/v1/check");
            body.Should().Contain("\"profile\":\"Hebrew\"").And.Contain("\"language\":\"Hebrew\"").And.Contain("\"description\":\"d\"").And.Contain("Animation");
        }

        private string SentBody()
        {
            Mocker.GetMock<IHttpClient>().Verify(c => c.Execute(It.IsAny<HttpRequest>()), Times.AtLeastOnce());
            var invocations = Mocker.GetMock<IHttpClient>().Invocations;
            var sent = (HttpRequest)invocations[invocations.Count - 1].Arguments[0];
            return System.Text.Encoding.UTF8.GetString(sent.ContentData);
        }

        [Test]
        public void should_not_send_null_fields()
        {
            GivenResponse("{\"verdict\":\"has\"}");
            var release = new ReleaseInfo { Guid = "g1", Title = "Frozen.2013.1080p", DownloadUrl = "http://prowlarr/dl/1" };

            Subject.Check(release, _movie, Language.Hebrew);

            SentBody().Should().NotContain("null");
        }

        [Test]
        public void should_not_cache_unknown()
        {
            GivenResponse("{\"verdict\":\"unknown\"}");

            Subject.Check(Release(), _movie, Language.Hebrew);
            Subject.Check(Release(), _movie, Language.Hebrew);

            Mocker.GetMock<IHttpClient>().Verify(c => c.Execute(It.IsAny<HttpRequest>()), Times.Exactly(2));
        }

        [Test]
        public void should_check_cache_only_after_prefetch()
        {
            GivenResponse("{\"verdict\":\"unknown\",\"checked\":1,\"pending\":0}");
            var release = Release("p1");
            release.DownloadUrl = "http://prowlarr/dl/p1";

            Subject.Prefetch(new List<ReleaseInfo> { release }, new MovieSearchCriteria { Movie = _movie });
            Subject.Check(release, _movie, Language.Hebrew);

            SentBody().Should().Contain("\"cacheOnly\":true");
        }

        [Test]
        public void should_check_cache_only_without_download_url()
        {
            GivenResponse("{\"verdict\":\"unknown\"}");

            Subject.Check(new ReleaseInfo { Guid = "q1", Title = "Frozen.2013.1080p" }, _movie, Language.Hebrew);

            SentBody().Should().Contain("\"cacheOnly\":true");
        }

        [Test]
        public void should_do_full_check_for_unprefetched_release()
        {
            GivenResponse("{\"verdict\":\"unknown\"}");
            var release = Release("r1");
            release.DownloadUrl = "http://prowlarr/dl/r1";

            Subject.Check(release, _movie, Language.Hebrew);

            SentBody().Should().Contain("\"cacheOnly\":false");
        }

        [Test]
        public void should_cache_per_guid_and_language()
        {
            GivenResponse("{\"verdict\":\"has\"}");

            Subject.Check(Release(), _movie, Language.Hebrew);
            Subject.Check(Release(), _movie, Language.Hebrew);

            Mocker.GetMock<IHttpClient>().Verify(c => c.Execute(It.IsAny<HttpRequest>()), Times.Once());
        }

        [Test]
        public void should_be_unknown_without_url()
        {
            Environment.SetEnvironmentVariable("DUBCHECK_URL", null);

            Subject.Check(Release(), _movie, Language.Hebrew).Should().Be(DubVerdict.Unknown);
            Mocker.GetMock<IHttpClient>().Verify(c => c.Execute(It.IsAny<HttpRequest>()), Times.Never());
        }

        [Test]
        public void should_open_breaker_after_three_failures()
        {
            Mocker.GetMock<IHttpClient>()
                .Setup(c => c.Execute(It.IsAny<HttpRequest>()))
                .Throws(new WebException("refused"));

            foreach (var guid in new[] { "a", "b", "c", "d" })
            {
                Subject.Check(Release(guid), _movie, Language.Hebrew).Should().Be(DubVerdict.Unknown);
            }

            Mocker.GetMock<IHttpClient>().Verify(c => c.Execute(It.IsAny<HttpRequest>()), Times.Exactly(3));
        }

        [Test]
        public void should_not_cache_failures()
        {
            Mocker.GetMock<IHttpClient>()
                .SetupSequence(c => c.Execute(It.IsAny<HttpRequest>()))
                .Throws(new WebException("refused"))
                .Returns(new HttpResponse(new HttpRequest("http://dubcheck:9395/v1/check"), new HttpHeader(), "{\"verdict\":\"has\"}"));

            Subject.Check(Release(), _movie, Language.Hebrew).Should().Be(DubVerdict.Unknown);
            Subject.Check(Release(), _movie, Language.Hebrew).Should().Be(DubVerdict.Has);
        }

        [Test]
        public void should_prefetch_once_for_a_search()
        {
            GivenResponse("{\"checked\":2,\"pending\":0}");
            var criteria = new MovieSearchCriteria { Movie = _movie };

            Subject.Prefetch(new List<ReleaseInfo> { Release("a"), Release("b") }, criteria);

            Mocker.GetMock<IHttpClient>().Verify(c => c.Execute(It.Is<HttpRequest>(r => r.Url.FullUri.EndsWith("/v1/prefetch"))), Times.Once());
        }

        [Test]
        public void should_skip_when_profile_does_not_score_dubcheck()
        {
            _movie.QualityProfile = DubProfile("Any", 0);

            Subject.Check(Release(), _movie, Language.Hebrew).Should().Be(DubVerdict.Unknown);
            Mocker.GetMock<IHttpClient>().Verify(c => c.Execute(It.IsAny<HttpRequest>()), Times.Never());
        }

        [Test]
        public void should_load_profile_when_movie_has_none()
        {
            GivenResponse("{\"verdict\":\"has\"}");
            _movie.QualityProfileId = 11;
            var profile = DubProfile("Hebrew", 20000);
            _movie.QualityProfile = null;
            Mocker.GetMock<IQualityProfileService>().Setup(s => s.Get(11)).Returns(profile);

            Subject.Check(Release(), _movie, Language.Hebrew).Should().Be(DubVerdict.Has);
        }

        [Test]
        public void should_use_hydrated_profile_not_the_joined_one()
        {
            // MovieRepository joins QualityProfile without its custom formats' specifications
            GivenResponse("{\"verdict\":\"has\"}");
            _movie.QualityProfileId = 11;
            _movie.QualityProfile = new QualityProfile
            {
                Id = 11,
                Name = "Hebrew",
                FormatItems = new List<ProfileFormatItem> { new ProfileFormatItem { Format = new CustomFormat { Id = 5 }, Score = 20000 } }
            };
            Mocker.GetMock<IQualityProfileService>().Setup(s => s.Get(11)).Returns(DubProfile("Hebrew", 20000));

            Subject.Check(Release(), _movie, Language.Hebrew).Should().Be(DubVerdict.Has);
        }

        [Test]
        public void should_not_prefetch_without_search()
        {
            Subject.Prefetch(new List<ReleaseInfo> { Release("a") }, null);

            Mocker.GetMock<IHttpClient>().Verify(c => c.Execute(It.IsAny<HttpRequest>()), Times.Never());
        }
    }
}
