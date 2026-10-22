using ACP.NINA.Plugin.Models;
using ACP.NINA.Plugin.Services;
using ACP.NINA.Plugin.Services.TargetScheduler;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace ACP.NINA.Plugin.Tests {

    /// An interface stand-in that answers each call from a delegate and
    /// returns the type's default for anything it does not answer.
    public class FakeProxy : DispatchProxy {
        public Func<MethodInfo, object> Handler;

        protected override object Invoke(MethodInfo method, object[] args) {
            var answer = Handler?.Invoke(method);
            if (answer != null) return answer;
            var type = method.ReturnType;
            return type != typeof(void) && type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        public static object Make(Type contract, Func<MethodInfo, object> handler) {
            var create = typeof(DispatchProxy).GetMethods()
                .First(m => m.Name == "Create" && m.GetGenericArguments().Length == 2)
                .MakeGenericMethod(contract, typeof(FakeProxy));
            var proxy = create.Invoke(null, null);
            ((FakeProxy)proxy).Handler = handler;
            return proxy;
        }
    }

    public class TsFingerprintBuildTests {

        // -- L1 through Build ------------------------------------------------

        [Fact]
        public void L1_BuildTakesTheProfileIdAndSlotsFromTheProfileWithNoWheelConnected() {
            // NINA's observable collection posts its changes to the current
            // synchronisation context, which a test thread does not have.
            // NINA keeps the one it uses in a static member of its core
            // assembly, found here by type so the test does not name it.
            var context = new System.Threading.SynchronizationContext();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (var type in typeof(global::NINA.Core.Utility.Logger).Assembly.GetTypes()) {
                if (type.ContainsGenericParameters) continue;
                foreach (var prop in type.GetProperties(flags)) {
                    if (prop.PropertyType == typeof(System.Threading.SynchronizationContext) && prop.CanWrite) {
                        prop.SetValue(null, context);
                    }
                }
                foreach (var field in type.GetFields(flags)) {
                    if (field.FieldType == typeof(System.Threading.SynchronizationContext) && !field.IsInitOnly) {
                        field.SetValue(null, context);
                    }
                }
            }
            var previous = System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            try {
                RunBuildCheck();
            } finally {
                System.Threading.SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        private static void RunBuildCheck() {
            var slots = new[] {
                Tuple.Create("H", 4), Tuple.Create("O", 5), Tuple.Create("S", 6), Tuple.Create("L", 0),
                Tuple.Create("R", 1), Tuple.Create("G", 2), Tuple.Create("B", 3),
            };
            var profileId = Guid.NewGuid();

            var wheelSettings = new Func<MethodInfo, object>(m => {
                if (m.Name != "get_FilterWheelFilters") return null;
                var itemType = m.ReturnType.GetGenericArguments()[0];
                var items = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType));
                foreach (var slot in slots) {
                    var item = Activator.CreateInstance(itemType);
                    itemType.GetProperty("Name").SetValue(item, slot.Item1);
                    var position = itemType.GetProperty("Position");
                    position.SetValue(item, Convert.ChangeType(slot.Item2, position.PropertyType));
                    items.Add(item);
                }
                // Built from a list so no change is posted to NINA's UI context.
                return Activator.CreateInstance(m.ReturnType, items);
            });
            var profile = new Func<MethodInfo, object>(m => {
                switch (m.Name) {
                    case "get_Id": return profileId;
                    case "get_Name": return "Foo";
                    case "get_FilterWheelSettings": return FakeProxy.Make(m.ReturnType, wheelSettings);
                    default: return null;
                }
            });
            var profileService = (IProfileService)FakeProxy.Make(typeof(IProfileService), m =>
                m.Name == "get_ActiveProfile" ? FakeProxy.Make(m.ReturnType, profile) : null);
            // Nothing is connected: every mediator reports no device.
            var service = new GearFingerprintService(
                profileService,
                (ICameraMediator)FakeProxy.Make(typeof(ICameraMediator), m => null),
                (IFilterWheelMediator)FakeProxy.Make(typeof(IFilterWheelMediator), m => null),
                (ITelescopeMediator)FakeProxy.Make(typeof(ITelescopeMediator), m => null),
                () => 1);

            var fingerprint = service.Build(null);

            Assert.Equal(profileId.ToString(), fingerprint.ProfileId);
            Assert.Equal(new[] { "L", "R", "G", "B", "H", "O", "S" }, fingerprint.WheelSlots);
            Assert.Empty(fingerprint.Filters);
        }
    }
}
