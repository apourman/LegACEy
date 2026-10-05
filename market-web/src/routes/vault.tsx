import { Vault } from '../Vault';

// The Vault still loads in the browser, through the /api/* proxy
export default function VaultRoute() {
  return <Vault />;
}
