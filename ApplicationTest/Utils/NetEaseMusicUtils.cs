﻿using Application.Bean;
using NUnit.Framework;
using static Application.Utils.GlobalUtils;

namespace ApplicationTest.Utils
{
    [TestFixture]
    public class NetEaseMusicUtilsTest
    {
        [Test]
        public void GetOutputNameTest()
        {
            var songVo = new SongVo()
            {
                Name = "name",
                Singer = "singer"
            };
            Assert.AreEqual("name - singer",
                GetOutputName(songVo, new SearchInfo() { OutputFileNameType = OutputFilenameTypeEnum.NAME_SINGER }));
            Assert.AreEqual("singer - name",
                GetOutputName(songVo, new SearchInfo() { OutputFileNameType = OutputFilenameTypeEnum.SINGER_NAME }));
            Assert.AreEqual("name",
                GetOutputName(songVo, new SearchInfo() { OutputFileNameType = OutputFilenameTypeEnum.NAME }));
        }
    }
}