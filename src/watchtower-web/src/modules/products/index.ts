import type { AppModule } from '@/platform/app-module'
import { productsManifest, productsRoute, productNewRoute, productDetailRoute } from './module'

const productsModule = {
  manifest: productsManifest,
  routes: [productsRoute, productNewRoute, productDetailRoute],
} satisfies AppModule

export default productsModule
