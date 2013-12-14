using System;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using GalaSoft.MvvmLight.Messaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TestApp.SL
{
    [TestClass]
    public class GalasoftMessengerTests
    {
        private Messenger _target;
        private bool _receivedMessage;
        private Listener _listenerStrongRef;
        private WeakReference _listenerWeakReference;

        [TestInitialize]
        public void Initialize()
        {
            _target = new Messenger();
            _receivedMessage = false;

            _listenerStrongRef = new Listener
            {
                _testObject = this
            };
            _listenerWeakReference = new WeakReference(_listenerStrongRef);
            _listenerStrongRef.Subscribe(_target);
        }

        [TestMethod]
        public void DeliversMessage()
        {
            _target.Send(new object());

            Assert.IsTrue(_receivedMessage);
        }

        [TestMethod]
        public void DeliversMessageAfterGC()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            _target.Send(new object());

            Assert.IsTrue(_receivedMessage);
        }

        [TestMethod]
        public void DoesNotPreventGC()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            _target.Send(new object());

            Assert.IsTrue(_listenerWeakReference.IsAlive);
            Assert.IsTrue(_receivedMessage);

            // Remove the reference that is supposed to be the last and run GC
            _receivedMessage = false;
            _listenerStrongRef = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.IsFalse(_listenerWeakReference.IsAlive);
            _target.Send(new object());
            Assert.IsFalse(_receivedMessage);
        }

        private class Listener
        {
            public GalasoftMessengerTests _testObject;

            private void ReceiveMessage(object x)
            {
                _testObject._receivedMessage = true;
            }

            public void Subscribe(Messenger messenger)
            {
                messenger.Register<object>(this, ReceiveMessage);
            }
        }
    }
}
