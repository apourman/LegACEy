import type { Config } from '@react-router/dev/config';

// Framework mode: the website renders on its own server (the BFF). The app's code stays in src/.
export default {
  appDirectory: 'src',
  ssr: true,
} satisfies Config;
