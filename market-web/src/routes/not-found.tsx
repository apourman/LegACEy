import { data, Link } from 'react-router';

export function loader() {
  return data(null, { status: 404 });
}

export default function NotFound() {
  return <section><h1>Page not found</h1><Link to="/">Back to browse</Link></section>;
}
