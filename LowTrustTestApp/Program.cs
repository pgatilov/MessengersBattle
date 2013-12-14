using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GalaSoft.MvvmLight.Messaging;

namespace LowTrustTestApp
{
    class Program
    {
        static void Main(string[] args)
        {
            var listener = new Listener();
            listener.Subscribe();

            Messenger.Default.Send(new object());

            Console.ReadLine();
        }

        private class Listener
        {
            public void Subscribe()
            {
                Messenger.Default.Register<object>(this, x => Console.WriteLine("Ping"));
            }
        }
    }
}
