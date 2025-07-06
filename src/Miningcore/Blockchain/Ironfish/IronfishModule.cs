using Autofac;
using Miningcore.Blockchain.Ironfish;
using Miningcore.Mining;
using Miningcore.Payments;

namespace Miningcore.Blockchain.Ironfish
{
    public class IronfishModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);

            // Register Ironfish-specific components
            builder.RegisterType<IronfishJob>()
                .AsSelf()
                .InstancePerDependency();

            builder.RegisterType<IronfishJobManager>()
                .AsSelf()
                .InstancePerDependency();

            builder.RegisterType<IronfishPool>()
                .As<IMiningPool>()
                .Keyed<IMiningPool>(CoinFamily.Ironfish)
                .InstancePerDependency();

            builder.RegisterType<IronfishPaymentProcessor>()
                .Keyed<IPaymentProcessor>(CoinFamily.Ironfish)
                .InstancePerDependency();

            builder.RegisterType<IronfishWorkerContext>()
                .AsSelf()
                .InstancePerDependency();

            builder.RegisterType<IronfishExtraNonceProvider>()
                .As<IExtraNonceProvider>()
                .InstancePerDependency();
        }
    }
}

