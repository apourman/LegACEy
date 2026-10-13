using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    [TestClass]
    public class InscriptionTests
    {
        [DataTestMethod]
        [DataRow("")]
        [DataRow(null)]
        [DataRow("Bonked many a drudge")]
        [DataRow("Pour Ilsa, d'Yaraq — ça va!")]
        public void PlainTextAndErasingAreAccepted(string text)
        {
            Assert.IsNull(Player.InscriptionTextRefusal(text));
        }

        [TestMethod]
        public void TheRetailFieldsLengthIsTheLimit()
        {
            Assert.IsNull(Player.InscriptionTextRefusal(new string('a', Player.MaxInscriptionLength)));
            Assert.IsNotNull(Player.InscriptionTextRefusal(new string('a', Player.MaxInscriptionLength + 1)));
        }

        [DataTestMethod]
        [DataRow("Mine\nInscribed by Asheron")]
        [DataRow("Mine\r\nInscribed by Asheron")]
        [DataRow("Mine\0")]
        [DataRow("Mine‮eno")]   // right-to-left override
        [DataRow("Mi​ne")]      // zero-width space
        public void LineBreaksAndHiddenCharactersAreRefused(string text)
        {
            Assert.IsNotNull(Player.InscriptionTextRefusal(text));
        }
    }
}
