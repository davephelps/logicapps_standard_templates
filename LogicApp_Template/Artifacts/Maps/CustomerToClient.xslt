<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="3.0" 
    xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
    xmlns:src="http://EDITest.SourceCustomer"
    xmlns:tgt="http://EDITest.TargetCustomer"
    xmlns:xs="http://www.w3.org/2001/XMLSchema"
    exclude-result-prefixes="src xs">
    
    <xsl:output method="xml" indent="yes" encoding="UTF-8"/>
    
    <xsl:template match="/">
        <tgt:Clientlist>
            <xsl:apply-templates select="src:Customer/src:CustomerDetail"/>
        </tgt:Clientlist>
    </xsl:template>
    
    <xsl:template match="src:CustomerDetail">
        <tgt:Client>
            <tgt:FullName>
                <xsl:value-of select="concat(src:Name, ' ', src:Surname)"/>
            </tgt:FullName>
            <tgt:BirthDate>
                <xsl:value-of select="src:DateOfBirth"/>
            </tgt:BirthDate>
            <tgt:Forename>
                <xsl:value-of select="src:Name"/>
            </tgt:Forename>
            <tgt:Surname>
                <xsl:value-of select="src:Surname"/>
            </tgt:Surname>
            <tgt:AccountType>Standard</tgt:AccountType>
            <tgt:Title>
                <xsl:value-of select="src:Title"/>
            </tgt:Title>
            <tgt:AddressDetails>
                <tgt:streetname>
                    <xsl:value-of select="src:Address/src:addressline1"/>
                </tgt:streetname>
            </tgt:AddressDetails>
        </tgt:Client>
    </xsl:template>
    
</xsl:stylesheet>
