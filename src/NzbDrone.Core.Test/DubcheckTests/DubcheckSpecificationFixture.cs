using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Dubcheck;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.DubcheckTests
{
    [TestFixture]
    public class DubcheckSpecificationFixture : CoreTest<DubcheckSpecification>
    {
        private Mock<IDubcheckClient> _client;
        private CustomFormatInput _input;

        [SetUp]
        public void Setup()
        {
            _client = new Mock<IDubcheckClient>();
            DubcheckSpecification.Client = _client.Object;
            _input = new CustomFormatInput
            {
                MovieInfo = Builder<ParsedMovieInfo>.CreateNew().Build(),
                Movie = Builder<Movie>.CreateNew().Build(),
                Release = new ReleaseInfo { Guid = "g1", Title = "Frozen.2013.1080p" }
            };
            Subject.Value = Language.Hebrew.Id;
        }

        [TearDown]
        public void TearDown()
        {
            DubcheckSpecification.Client = null;
        }

        private void GivenVerdict(DubVerdict verdict)
        {
            _client.Setup(c => c.Check(It.IsAny<ReleaseInfo>(), It.IsAny<Movie>(), Language.Hebrew)).Returns(verdict);
        }

        [Test]
        public void should_not_match_without_release()
        {
            GivenVerdict(DubVerdict.Has);
            _input.Release = null;

            Subject.IsSatisfiedBy(_input).Should().BeFalse();
            _client.Verify(c => c.Check(It.IsAny<ReleaseInfo>(), It.IsAny<Movie>(), It.IsAny<Language>()), Times.Never());
        }

        [Test]
        public void should_match_has_when_verdict_has()
        {
            GivenVerdict(DubVerdict.Has);
            Subject.Verdict = (int)DubVerdict.Has;

            Subject.IsSatisfiedBy(_input).Should().BeTrue();
        }

        [Test]
        public void should_not_match_lacks_when_verdict_has()
        {
            GivenVerdict(DubVerdict.Has);
            Subject.Verdict = (int)DubVerdict.Lacks;

            Subject.IsSatisfiedBy(_input).Should().BeFalse();
        }

        [Test]
        public void should_match_lacks_when_verdict_lacks()
        {
            GivenVerdict(DubVerdict.Lacks);
            Subject.Verdict = (int)DubVerdict.Lacks;

            Subject.IsSatisfiedBy(_input).Should().BeTrue();
        }

        [Test]
        public void should_not_match_when_unknown()
        {
            GivenVerdict(DubVerdict.Unknown);
            Subject.Verdict = (int)DubVerdict.Has;

            Subject.IsSatisfiedBy(_input).Should().BeFalse();
        }

        [Test]
        public void should_not_match_without_client()
        {
            DubcheckSpecification.Client = null;
            Subject.Verdict = (int)DubVerdict.Has;

            Subject.IsSatisfiedBy(_input).Should().BeFalse();
        }
    }
}
