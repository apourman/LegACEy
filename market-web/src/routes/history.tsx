import { History } from '../History';

// History still loads in the browser, through the /api/* proxy
export default function HistoryRoute() {
  return <History />;
}
