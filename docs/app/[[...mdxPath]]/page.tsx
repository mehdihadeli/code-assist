import { generateStaticParamsFor, importPage } from 'nextra/pages';

export const generateStaticParams = generateStaticParamsFor('mdxPath');
export const dynamicParams = false;

type PageProps = { params: Promise<{ mdxPath?: string[] }> };

export async function generateMetadata({ params }: PageProps) {
  const { mdxPath } = await params;
  const { metadata } = await importPage(mdxPath);
  return metadata;
}

export default async function Page({ params }: PageProps) {
  const { mdxPath } = await params;
  const { default: Content } = await importPage(mdxPath);
  return <Content params={await params} />;
}